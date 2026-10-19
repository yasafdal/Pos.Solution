using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public interface IOfflineCheckoutService
{
    Task<bool> IsReadyForOfflineCheckoutAsync(int outletId);
    Task<OfflineCheckoutResultDto> ProcessOfflineCashSaleAsync(OrderCreateDto createDto, decimal cashTendered, string cashierId);
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

    public async Task<bool> IsReadyForOfflineCheckoutAsync(int outletId)
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        if (!db.VerifyIntegrity()) return false;

        var hasProducts = await db.Products.AnyAsync(p => p.IsActive);
        return hasProducts;
    }

    public async Task<OfflineCheckoutResultDto> ProcessOfflineCashSaleAsync(OrderCreateDto createDto, decimal cashTendered, string cashierId)
    {
        if (createDto.Items == null || createDto.Items.Count == 0)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Order must contain at least one item." };
        }

        if (cashTendered <= 0m)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "A valid cash tendered amount is required for offline checkout." };
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
                // 1. Fetch outlet config for tax type
                var outletConfig = await db.OutletConfigs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.OutletId == createDto.OutletId);
                string branchTaxType = outletConfig?.BranchTaxType ?? "VAT-Registered";

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

                if (cashTendered < totalAmount)
                {
                    await transaction.RollbackAsync();
                    return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Insufficient cash tendered. Total payable: ₱{totalAmount:N2}" };
                }

                decimal changeAmount = cashTendered - totalAmount;

                // 3. Acquire and increment invoice sequence atomically inside the SQLite transaction
                var sequence = await db.InvoiceSequences
                    .FirstOrDefaultAsync(s => s.OutletId == createDto.OutletId && s.IsActive);

                if (sequence == null)
                {
                    sequence = new LocalInvoiceSequence
                    {
                        OutletId = createDto.OutletId,
                        SeriesCode = $"OFF-SER-{createDto.OutletId:D2}",
                        Prefix = $"INV{createDto.OutletId:D2}-",
                        StartingNumber = 1L,
                        EndingNumber = 9999999L,
                        LastIssuedNumber = 0L,
                        PaddingLength = 7,
                        IsActive = true
                    };
                    db.InvoiceSequences.Add(sequence);
                }
                else if (sequence.LastIssuedNumber >= sequence.EndingNumber)
                {
                    // Cleanly refill local sequence block so active offline checkouts never abort
                    sequence.EndingNumber = sequence.LastIssuedNumber + 100000L;
                }

                sequence.LastIssuedNumber++;
                long nextNum = sequence.LastIssuedNumber;
                int padLen = sequence.PaddingLength > 0 ? sequence.PaddingLength : 7;
                string prefix = string.IsNullOrWhiteSpace(sequence.Prefix) ? $"INV{createDto.OutletId:D2}-" : sequence.Prefix;
                string invoiceNumber = $"{prefix}{nextNum.ToString().PadLeft(padLen, '0')}";

                // 4. Persist Local Order
                string localTransactionId = Guid.NewGuid().ToString();
                var localOrder = new LocalOrder
                {
                    OrderId = localTransactionId,
                    InvoiceNumber = invoiceNumber,
                    BranchId = createDto.BranchId,
                    OutletId = createDto.OutletId,
                    CashierId = cashierId,
                    Subtotal = subtotal,
                    DiscountAmount = calcRes.DiscountAmount,
                    DiscountCode = createDto.DiscountCode,
                    VatableSales = calcRes.VatableSales,
                    VatExemptSales = calcRes.VatExemptSales,
                    ZeroRatedSales = calcRes.ZeroRatedSales,
                    VatAmount = calcRes.VatAmount,
                    TotalAmount = totalAmount,
                    PaymentStatus = "SUCCESS",
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

                    db.OrderItems.Add(new LocalOrderItem
                    {
                        OrderId = localTransactionId,
                        ProductId = itemDto.ProductId,
                        ProductName = itemDto.ProductName ?? "Product",
                        Quantity = (int)itemDto.Quantity,
                        UnitPrice = itemEffPrice,
                        LineTotal = lineTotal,
                        TaxName = itemDto.TaxName ?? "VATable"
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
                    TenderedAmount = cashTendered,
                    ChangeAmount = changeAmount,
                    PaymentMethod = "CASH",
                    PaymentStatus = "SUCCESS",
                    PaidAt = paidAt
                });

                // 6b. Update active LocalCashDrawerShift so offline ExpectedBalance reflects offline cash sales immediately
                var localOpenShift = await db.CashDrawerShifts
                    .Where(s => s.OutletId == createDto.OutletId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync();
                if (localOpenShift != null)
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

                var sessionOutlet = _session.Outlet;
                string branchName = !string.IsNullOrWhiteSpace(outletConfig?.BranchName)
                    ? outletConfig.BranchName!
                    : (!string.IsNullOrWhiteSpace(sessionOutlet?.BranchName) ? sessionOutlet.BranchName : (outletConfig?.OutletName ?? sessionOutlet?.OutletName ?? string.Empty));
                string? branchCode = !string.IsNullOrWhiteSpace(outletConfig?.BranchCode)
                    ? outletConfig.BranchCode
                    : sessionOutlet?.BranchCode;
                string? branchAddress = !string.IsNullOrWhiteSpace(outletConfig?.BranchAddress)
                    ? outletConfig.BranchAddress
                    : sessionOutlet?.BranchAddress;
                string? branchPhone = !string.IsNullOrWhiteSpace(outletConfig?.BranchPhone)
                    ? outletConfig.BranchPhone
                    : sessionOutlet?.BranchPhone;
                string terminalName = !string.IsNullOrWhiteSpace(outletConfig?.OutletName)
                    ? outletConfig.OutletName
                    : (sessionOutlet?.OutletName ?? string.Empty);

                var offlineReceipt = new ReceiptDto
                {
                    OrderId = 0,
                    OrderNumber = $"OFF-{localTransactionId[..8].ToUpperInvariant()}",
                    BranchId = createDto.BranchId,
                    BranchName = branchName,
                    BranchCode = branchCode,
                    BranchAddress = branchAddress,
                    BranchPhone = branchPhone,
                    OutletId = createDto.OutletId,
                    TerminalName = terminalName,
                    CashierId = cashierId,
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
                            PaymentMethod = "CASH",
                            Amount = totalAmount,
                            AmountTendered = cashTendered,
                            ChangeAmount = changeAmount,
                            PaymentStatus = "SUCCESS",
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
            .Where(o => o.PaymentStatus == "SUCCESS")
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }
}
