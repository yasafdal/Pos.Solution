using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public interface IOfflineCheckoutService
{
    Task<bool> IsReadyForOfflineCheckoutAsync(int terminalId);
    Task<OfflineCheckoutResultDto> ProcessOfflineCashSaleAsync(OrderCreateDto createDto, decimal cashTendered, string cashierId);
    Task<OfflineCheckoutResultDto> ProcessSaleAsync(OrderCreateDto createDto, decimal amountTendered, string paymentMethod, string? referenceNumber, string cashierId);
    Task<List<LocalOrder>> GetPendingSyncOrdersAsync();
}

public class OfflineCheckoutResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LocalTransactionId { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal ChangeAmount { get; set; }
    public ReceiptDto? Receipt { get; set; }
}

public class OfflineCheckoutService : IOfflineCheckoutService
{
    private readonly LocalPosDbContext _localDb;
    private readonly PosSessionService _session;

    public OfflineCheckoutService(LocalPosDbContext localDb, PosSessionService session)
    {
        _localDb = localDb;
        _session = session;
    }

    public static string FormatOrderNumber(string? terminalName, int terminalId, long sequenceNumber)
    {
        string codePrefix = !string.IsNullOrWhiteSpace(terminalName)
            ? terminalName.Replace(" ", "").ToUpperInvariant()
            : $"POS{terminalId}";
        if (codePrefix.StartsWith("POS", StringComparison.Ordinal) && codePrefix.Length == 4 && char.IsDigit(codePrefix[3]))
            codePrefix = "POS0" + codePrefix[3];
        return $"{codePrefix}-{sequenceNumber:D6}";
    }

    public async Task<bool> IsReadyForOfflineCheckoutAsync(int terminalId)
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        if (!db.VerifyIntegrity()) return false;

        var hasProducts = await db.Products.AnyAsync(p => p.IsActive);
        return hasProducts && await db.TerminalConfigs.AnyAsync(t => t.TerminalId == terminalId && t.OutletId > 0)
            && await db.InvoiceSequences.AnyAsync(s => s.TerminalId == terminalId && s.IsActive && s.LastIssuedNumber < s.EndingNumber);
    }

    public Task<OfflineCheckoutResultDto> ProcessOfflineCashSaleAsync(OrderCreateDto createDto, decimal cashTendered, string cashierId)
    {
        return ProcessSaleAsync(createDto, cashTendered, "CASH", null, cashierId);
    }

    public async Task<OfflineCheckoutResultDto> ProcessSaleAsync(OrderCreateDto createDto, decimal amountTendered, string paymentMethod, string? referenceNumber, string cashierId)
    {
        paymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "CASH" : paymentMethod.Trim().ToUpperInvariant();
        if (!_session.IsAuthenticated || !string.Equals(_session.Username, cashierId, StringComparison.Ordinal)
            || _session.Terminal?.TerminalId != createDto.TerminalId
            || _session.Terminal?.BranchId != createDto.BranchId
            || (!string.IsNullOrEmpty(createDto.CashierId) && !string.Equals(createDto.CashierId, cashierId, StringComparison.Ordinal)))
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Checkout requires the authenticated cashier and assigned terminal/branch." };

        if (createDto.Items == null || createDto.Items.Count == 0)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Order must contain at least one item." };
        }

        if (paymentMethod == "CASH" && amountTendered <= 0m)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "A valid cash tendered amount is required for checkout." };
        }

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            // Begin SQLite atomic transaction
            using var transaction = await db.Database.BeginTransactionAsync();
            try
            {
                var confirmedShift = await db.CashDrawerShifts.SingleOrDefaultAsync(s => s.TerminalId == createDto.TerminalId && s.BranchId == createDto.BranchId && s.CashierId == cashierId && s.Status == "OPEN" && s.OpeningConfirmed);
                if (confirmedShift == null || (createDto.ShiftId.HasValue && createDto.ShiftId != confirmedShift.ShiftId))
                    throw new InvalidOperationException("Open a shift and confirm opening float before offline payment.");
                createDto.ShiftId = confirmedShift.ShiftId;
                createDto.ClientShiftId = confirmedShift.ClientShiftId;
                // 1. Fetch outlet config for tax type
                var outletConfig = await db.TerminalConfigs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.TerminalId == createDto.TerminalId);
                if (outletConfig == null || outletConfig.OutletId <= 0 || outletConfig.BranchId != createDto.BranchId)
                    throw new InvalidOperationException("Terminal ownership must be refreshed online before offline checkout.");

                var lockStatus = BusinessDayClock.EvaluateLock(
                    terminalId: outletConfig.TerminalId,
                    closedBusinessDate: outletConfig.ClosedBusinessDate,
                    lockedByCashier: outletConfig.ClosingLockedByCashier,
                    lockedAtUtc: outletConfig.ClosingLockedAtUtc,
                    unlockAtUtc: outletConfig.ClosingUnlockAtUtc,
                    candidateCashierUsername: cashierId,
                    authoritativeServerUtc: null,
                    deviceUtcNow: BusinessDayClock.UtcNow(),
                    currentTickCount64: BusinessDayClock.TickCount64(),
                    lockedAtTickCount64: outletConfig.ClosingLockedTickCount64,
                    lastKnownServerUtc: outletConfig.LastKnownServerUtc,
                    lastKnownServerTickCount64: outletConfig.LastKnownServerTickCount64);
                if (lockStatus.IsCashierRestricted || lockStatus.IsTerminalLocked)
                    throw new InvalidOperationException(lockStatus.CashierRestrictionMessage ?? lockStatus.LockMessage ?? "POS Terminal is locked for the remainder of the current business day.");

                string branchTaxType = outletConfig.BranchTaxType;

                // 2. Calculate taxes and discounts first (before incrementing sequence) using decimal precision
                var calcReq = new OrderCalculationRequestDto
                {
                    BranchTaxType = branchTaxType,
                    DiscountCode = createDto.DiscountCode ?? "NONE",
                    Items = createDto.Items.Select(i => new CartItemCalculationDto
                    {
                        ProductId = i.ProductId,
                        ProductName = i.ProductName ?? "Product",
                        Quantity = i.Quantity,
                        UnitPrice = i.BasePrice,
                        ModifierTotal = i.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m,
                        TaxName = i.TaxName ?? "VATable",
                        IsEligibleForStatutoryDiscount = true
                    }).ToList()
                };

                var calculator = new LocalDiscountCalculationService();
                var calcRes = calculator.CalculateOrder(calcReq);

                decimal subtotal = calcRes.Subtotal;
                decimal totalAmount = calcRes.TotalAmount;

                decimal tendered = paymentMethod == "CASH" ? amountTendered : totalAmount;
                if (paymentMethod == "CASH" && tendered < totalAmount)
                {
                    await transaction.RollbackAsync();
                    return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Insufficient cash tendered. Total payable: ₱{totalAmount:N2}" };
                }

                decimal changeAmount = paymentMethod == "CASH" ? tendered - totalAmount : 0m;

                // 3. Acquire and increment invoice sequence and order sequence atomically inside the SQLite transaction
                var sequence = await db.InvoiceSequences
                    .FirstOrDefaultAsync(s => s.TerminalId == createDto.TerminalId && s.IsActive);

                if (sequence == null || sequence.LastIssuedNumber >= sequence.EndingNumber)
                    throw new InvalidOperationException("No remaining configured invoice range. Reconnect and configure invoice allocation; no sale was saved.");

                sequence.LastIssuedNumber++;
                long nextNum = sequence.LastIssuedNumber;
                int padLen = sequence.PaddingLength > 0 ? sequence.PaddingLength : 7;
                string prefix = string.IsNullOrWhiteSpace(sequence.Prefix) ? $"INV{createDto.TerminalId:D2}-" : sequence.Prefix;
                string invoiceNumber = $"{prefix}{nextNum.ToString().PadLeft(padLen, '0')}";

                var orderSeq = await db.OrderSequences
                    .FirstOrDefaultAsync(s => s.TerminalId == createDto.TerminalId);
                if (orderSeq == null)
                {
                    orderSeq = new LocalOrderSequence
                    {
                        TerminalId = createDto.TerminalId,
                        LastIssuedNumber = 0,
                        UpdatedAt = DateTime.UtcNow
                    };
                    db.OrderSequences.Add(orderSeq);
                }
                orderSeq.LastIssuedNumber++;
                orderSeq.UpdatedAt = DateTime.UtcNow;
                string orderNumber = FormatOrderNumber(outletConfig.TerminalName, createDto.TerminalId, orderSeq.LastIssuedNumber);

                // 4. Persist Local Order
                string localTransactionId = Guid.NewGuid().ToString();
                var localOrder = new LocalOrder
                {
                    OrderId = localTransactionId,
                    OrderNumber = orderNumber,
                    InvoiceNumber = invoiceNumber,
                    BranchId = createDto.BranchId,
                    OutletId = createDto.OutletId > 0 ? createDto.OutletId : outletConfig.OutletId,
                    TerminalId = createDto.TerminalId,
                    ShiftId = confirmedShift.ShiftId,
                    ClientShiftId = confirmedShift.ClientShiftId,
                    OrderType = createDto.OrderType ?? "DINING",
                    TableNumber = createDto.TableNumber,
                    HeadCount = createDto.HeadCount,
                    CashierId = cashierId,
                    Subtotal = subtotal,
                    DiscountAmount = calcRes.DiscountAmount,
                    DiscountCode = createDto.DiscountCode,
                    VatableSales = calcRes.VatableSales,
                    VatExemptSales = calcRes.VatExemptSales,
                    ZeroRatedSales = calcRes.ZeroRatedSales,
                    VatAmount = calcRes.VatAmount,
                    TotalAmount = totalAmount,
                    PaymentStatus = "PAID",
                    OrderStatus = "COMPLETED",
                    CreatedAt = DateTime.UtcNow
                };

                db.Orders.Add(localOrder);

                // 5. Persist Order Items (matched by index to preserve exact decimal totals across duplicate ProductIds)
                var receiptItems = new List<OrderItemResponseDto>();
                for (int idx = 0; idx < createDto.Items.Count; idx++)
                {
                    var itemDto = createDto.Items[idx];
                    decimal modifierTotal = itemDto.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m;
                    decimal itemEffPrice = itemDto.BasePrice + modifierTotal;
                    var calcItem = idx < calcRes.CalculatedItems.Count
                        ? calcRes.CalculatedItems[idx]
                        : calcRes.CalculatedItems.FirstOrDefault(ci => ci.ProductId == itemDto.ProductId);
                    decimal lineTotal = calcItem != null ? calcItem.LineTotal : Math.Round(itemEffPrice * itemDto.Quantity, 2);

                    var localProd = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.ProductId == itemDto.ProductId);
                    bool isKitchen = localProd?.IsKitchenItem ?? itemDto.IsKitchenItem;
                    bool isBar = localProd?.IsBarItem ?? itemDto.IsBarItem;
                    string? normalizedInstructions = CartItem.NormalizeSpecialInstructions(itemDto.SpecialInstructions);
                    itemDto.SpecialInstructions = normalizedInstructions;

                    db.OrderItems.Add(new LocalOrderItem
                    {
                        OrderId = localTransactionId,
                        ProductId = itemDto.ProductId,
                        ProductName = itemDto.ProductName ?? "Product",
                        ProductShortCode = itemDto.ProductShortCode,
                        Quantity = (int)itemDto.Quantity,
                        UnitPrice = itemEffPrice,
                        LineTotal = lineTotal,
                        TaxName = itemDto.TaxName ?? "VATable",
                        IsKitchenItem = isKitchen,
                        IsBarItem = isBar,
                        SpecialInstructions = normalizedInstructions,
                        ModifiersJson = itemDto.Modifiers != null && itemDto.Modifiers.Count > 0
                            ? System.Text.Json.JsonSerializer.Serialize(itemDto.Modifiers)
                            : null
                    });

                    receiptItems.Add(new OrderItemResponseDto
                    {
                        OrderItemId = idx + 1,
                        ProductId = itemDto.ProductId,
                        ProductName = itemDto.ProductName ?? "Product",
                        ProductShortCode = itemDto.ProductShortCode,
                        Quantity = itemDto.Quantity,
                        BasePrice = itemDto.BasePrice,
                        ModifierTotal = modifierTotal,
                        UnitPrice = itemEffPrice,
                        LineTotal = lineTotal,
                        VatType = calcItem?.VatType ?? "VATABLE",
                        VatAmount = calcItem?.VatAmount ?? 0m,
                        IsKitchenItem = isKitchen,
                        IsBarItem = isBar,
                        SpecialInstructions = normalizedInstructions,
                        Modifiers = itemDto.Modifiers?.Select((m, mIdx) => new OrderItemModifierResponseDto
                        {
                            OrderItemModifierId = mIdx + 1,
                            ModifierOptionId = m.ModifierOptionId,
                            ModifierName = m.ModifierName,
                            AdditionalPrice = m.AdditionalPrice
                        }).ToList() ?? new()
                    });
                }

                // 6. Persist Payment
                var paidAt = DateTime.UtcNow;
                db.Payments.Add(new LocalPayment
                {
                    OrderId = localTransactionId,
                    Amount = totalAmount,
                    TenderedAmount = tendered,
                    ChangeAmount = changeAmount,
                    PaymentMethod = paymentMethod,
                    PaymentStatus = "PAID",
                    PaidAt = paidAt
                });

                // 6b. Update active LocalCashDrawerShift so offline ExpectedBalance reflects cash sales immediately
                var localOpenShift = confirmedShift;
                if (localOpenShift != null && paymentMethod == "CASH")
                {
                    localOpenShift.TotalCashSales += totalAmount;
                    localOpenShift.ExpectedBalance = localOpenShift.OpeningFloat
                        + localOpenShift.TotalCashSales
                        - localOpenShift.TotalCashRefunds
                        + localOpenShift.TotalCashIn
                        - localOpenShift.TotalCashOut
                        - localOpenShift.TotalCashDrops;
                }

                // 7. Persist Sync Outbox Entry
                createDto.OfflineOrderNumber = orderNumber;
                createDto.OfflineInvoiceNumber = invoiceNumber;
                var outboxPayload = System.Text.Json.JsonSerializer.Serialize(createDto);
                db.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "Order",
                    EntityId = localTransactionId,
                    PayloadJson = outboxPayload,
                    Status = "PENDING",
                    RetryCount = 0,
                    CreatedAt = paidAt
                });

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                var sessionOutlet = _session.Terminal;
                string branchName = !string.IsNullOrWhiteSpace(outletConfig?.BranchName)
                    ? outletConfig.BranchName!
                    : (!string.IsNullOrWhiteSpace(sessionOutlet?.BranchName) ? sessionOutlet.BranchName : (outletConfig?.TerminalName ?? sessionOutlet?.TerminalName ?? string.Empty));
                string? branchCode = !string.IsNullOrWhiteSpace(outletConfig?.BranchCode)
                    ? outletConfig.BranchCode
                    : sessionOutlet?.BranchCode;
                string? branchAddress = !string.IsNullOrWhiteSpace(outletConfig?.BranchAddress)
                    ? outletConfig.BranchAddress
                    : sessionOutlet?.BranchAddress;
                string? branchPhone = !string.IsNullOrWhiteSpace(outletConfig?.BranchPhone)
                    ? outletConfig.BranchPhone
                    : sessionOutlet?.BranchPhone;
                string terminalName = !string.IsNullOrWhiteSpace(outletConfig?.TerminalName)
                    ? outletConfig.TerminalName
                    : (sessionOutlet?.TerminalName ?? string.Empty);

                var offlineReceipt = new ReceiptDto
                {
                    OrderId = 0,
                    OrderNumber = orderNumber,
                    BranchId = createDto.BranchId,
                    BranchName = branchName,
                    BranchCode = branchCode,
                    BranchAddress = branchAddress,
                    BranchPhone = branchPhone,
                    TerminalId = createDto.TerminalId,
                    OutletId = outletConfig.OutletId, OutletName = outletConfig.OutletName,
                    PTUNumber = outletConfig.PTUNumber, MINNumber = outletConfig.MINNumber, SerialNumber = outletConfig.SerialNumber,
                    TerminalName = terminalName,
                    CashierId = cashierId,
                    OrderType = createDto.OrderType ?? "DINING",
                    TableNumber = createDto.TableNumber,
                    HeadCount = createDto.HeadCount,
                    CreatedAt = localOrder.CreatedAt,
                    PaidAt = paidAt,
                    Subtotal = subtotal,
                    VatableSales = calcRes.VatableSales,
                    VatExemptSales = calcRes.VatExemptSales,
                    ZeroRatedSales = calcRes.ZeroRatedSales,
                    VatAmount = calcRes.VatAmount,
                    DiscountCode = createDto.DiscountCode,
                    CustomerCardNumber = createDto.CustomerCardNumber,
                    DiscountAmount = calcRes.DiscountAmount,
                    TotalAmount = totalAmount,
                    InvoiceNumber = invoiceNumber,
                    InvoiceIssuedAt = paidAt,
                    CustomerName = createDto.CustomerName,
                    CustomerAddress = createDto.CustomerAddress,
                    CustomerTin = createDto.CustomerTin,
                    CustomerBusinessStyle = createDto.CustomerBusinessStyle,
                    Items = receiptItems,
                    Payments = new List<PaymentResponseDto>
                    {
                        new()
                        {
                            PaymentMethod = paymentMethod,
                            Amount = totalAmount,
                            AmountTendered = tendered,
                            ChangeAmount = changeAmount,
                            PaymentStatus = "PAID",
                            ProcessedBy = cashierId,
                            PaidAt = paidAt
                        }
                    }
                };

                return new OfflineCheckoutResultDto
                {
                    Success = true,
                    LocalTransactionId = localTransactionId,
                    InvoiceNumber = invoiceNumber,
                    ChangeAmount = changeAmount,
                    Receipt = offlineReceipt
                };
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Offline checkout failed: {ex.Message}" };
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    public async Task<List<LocalOrder>> GetPendingSyncOrdersAsync()
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        return await db.Orders
            .AsNoTracking()
            .Where(o => o.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }
}
