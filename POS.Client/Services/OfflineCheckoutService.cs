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
    Task<ReceiptDto?> GetSavedReceiptByTransactionIdAsync(string localTransactionId, CancellationToken cancellationToken = default);
    Task<List<LocalOrder>> GetPendingSyncOrdersAsync();
}

public class OfflineCheckoutResultDto
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? LocalTransactionId { get; set; }
    public string? InvoiceNumber { get; set; }
    public decimal ChangeAmount { get; set; }
    public decimal CashAmountPaid { get; set; }
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
        if (!_session.IsAuthenticated || !string.Equals(_session.Username, cashierId, StringComparison.OrdinalIgnoreCase)
            || _session.Terminal?.TerminalId != createDto.TerminalId
            || _session.Terminal?.BranchId != createDto.BranchId
            || (_session.Terminal?.OutletId > 0 && createDto.OutletId > 0 && _session.Terminal.OutletId != createDto.OutletId)
            || (!string.IsNullOrEmpty(createDto.CashierId) && !string.Equals(createDto.CashierId, cashierId, StringComparison.OrdinalIgnoreCase)))
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Checkout requires the authenticated cashier and assigned terminal/branch." };

        if (createDto.Items == null || createDto.Items.Count == 0)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Order must contain at least one item." };
        }

        bool hasSplits = createDto.Splits is { Count: > 0 };
        if (!hasSplits && paymentMethod == "CASH" && amountTendered < 0m)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "A valid cash tendered amount is required for checkout." };
        }

        if (createDto.DiscountAmount < 0m || createDto.ManualDiscountPercentage < 0m || createDto.ManualDiscountPercentage > LocalDiscountCalculationService.MaxPercentageLimit
            || createDto.ManualDiscountAmount < 0m || createDto.PromoDiscountAmount < 0m)
        {
            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "Invalid discount value. Discount percentage must be between 0% and 100% and discount amounts cannot be negative." };
        }

        var licenseGate = await ClientLicenseService.CheckOperationalGateAsync(createDto.TerminalId);
        if (!licenseGate.Allowed)
        {
            return new OfflineCheckoutResultDto
            {
                Success = false,
                ErrorMessage = licenseGate.ErrorMessage ?? "Terminal license is not valid for processing new sales."
            };
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
                var confirmedShift = await ShiftSession.GetLocalActiveOpenShiftAsync(db, createDto.TerminalId);
                if (confirmedShift == null || !string.Equals(confirmedShift.Status, "OPEN", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Open a shift and confirm opening float before offline payment.");
                if (confirmedShift.BranchId != createDto.BranchId)
                    throw new InvalidOperationException("Open shift branch does not match the active terminal branch.");
                if (BusinessDayClock.IsPreviousBusinessDateShift(confirmedShift.OpenedAt, BusinessDayClock.UtcNow(), confirmedShift.BusinessDate))
                {
                    string origDate = BusinessDayClock.GetShiftBusinessDateString(confirmedShift.OpenedAt, confirmedShift.BusinessDate);
                    throw new InvalidOperationException(TerminalClosingLockDto.FormatUnresolvedPreviousDayShiftMessage(
                        origDate, confirmedShift.CashierId, confirmedShift.RecoveryStatus, confirmedShift.UnverifiedCashReason));
                }
                if (!string.Equals(confirmedShift.CashierId, cashierId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
                if (!confirmedShift.OpeningConfirmed || (createDto.ShiftId.HasValue && createDto.ShiftId != confirmedShift.ShiftId))
                    throw new InvalidOperationException("Open a shift and confirm opening float before offline payment.");
                createDto.ShiftId = confirmedShift.ShiftId;
                createDto.ClientShiftId = confirmedShift.ClientShiftId;
                // 1. Fetch outlet config for tax type
                var outletConfig = await db.TerminalConfigs
                    .AsNoTracking()
                    .FirstOrDefaultAsync(o => o.TerminalId == createDto.TerminalId);
                if (outletConfig == null || outletConfig.OutletId <= 0 || outletConfig.BranchId != createDto.BranchId ||
                    (createDto.OutletId > 0 && outletConfig.OutletId != createDto.OutletId) ||
                    (_session.Terminal?.OutletId > 0 && _session.Terminal.OutletId != outletConfig.OutletId))
                    throw new InvalidOperationException("Terminal ownership must be refreshed online before offline checkout.");

                var checkoutNowUtc = BusinessDayClock.UtcNow();
                var checkoutNowTicks = BusinessDayClock.TickCount64();
                var activeRestrictions = await ShiftSession.GetLocalActiveCashierRestrictionsAsync(db, outletConfig.TerminalId, checkoutNowUtc, checkoutNowTicks);
                var lockStatus = BusinessDayClock.EvaluateLock(
                    terminalId: outletConfig.TerminalId,
                    closedBusinessDate: outletConfig.ClosedBusinessDate,
                    lockedByCashier: outletConfig.ClosingLockedByCashier,
                    lockedAtUtc: outletConfig.ClosingLockedAtUtc,
                    unlockAtUtc: outletConfig.ClosingUnlockAtUtc,
                    candidateCashierUsername: cashierId,
                    authoritativeServerUtc: null,
                    deviceUtcNow: checkoutNowUtc,
                    currentTickCount64: checkoutNowTicks,
                    lockedAtTickCount64: outletConfig.ClosingLockedTickCount64,
                    lastKnownServerUtc: outletConfig.LastKnownServerUtc,
                    lastKnownServerTickCount64: outletConfig.LastKnownServerTickCount64,
                    additionalClosedCashiersToday: activeRestrictions.Select(r => r.CashierId));
                if (lockStatus.IsCashierRestricted || lockStatus.IsTerminalLocked)
                    throw new InvalidOperationException(lockStatus.CashierRestrictionMessage ?? lockStatus.LockMessage ?? "POS Terminal is locked for the remainder of the current business day.");

                var localBranch = await db.Branches
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.BranchId == outletConfig.BranchId);

                string branchTaxType = !string.IsNullOrWhiteSpace(localBranch?.TaxType)
                    ? localBranch.TaxType
                    : outletConfig.BranchTaxType;

                // 2. Calculate taxes and discounts first (before incrementing sequence) using decimal precision
                string normalizedDiscountCode = (createDto.DiscountCode ?? "NONE").Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(normalizedDiscountCode)) normalizedDiscountCode = "NONE";

                string? resolvedMethod = createDto.DiscountMethod;
                decimal? resolvedValue = createDto.DiscountValue;
                decimal resolvedPromoAmount = createDto.PromoDiscountAmount;

                // Validate discount activation status against latest locally synchronized SQLite records
                LocalDiscount? localDisc = null;
                if (createDto.DiscountTypeId.HasValue)
                {
                    localDisc = await db.Discounts.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.DiscountId == createDto.DiscountTypeId.Value);
                    if (localDisc != null && !localDisc.IsActive)
                    {
                        await transaction.RollbackAsync();
                        return new OfflineCheckoutResultDto
                        {
                            Success = false,
                            ErrorMessage = $"Discount '{localDisc.Name}' ({localDisc.Code}) is inactive and cannot be applied."
                        };
                    }
                }
                if (localDisc == null && normalizedDiscountCode != "NONE")
                {
                    localDisc = await db.Discounts.AsNoTracking()
                        .FirstOrDefaultAsync(d => d.Code.ToUpper() == normalizedDiscountCode);
                    if (localDisc != null && !localDisc.IsActive)
                    {
                        await transaction.RollbackAsync();
                        return new OfflineCheckoutResultDto
                        {
                            Success = false,
                            ErrorMessage = $"Discount '{localDisc.Name}' ({localDisc.Code}) is inactive and cannot be applied."
                        };
                    }
                }

                if (normalizedDiscountCode != "NONE" && normalizedDiscountCode != "MANUAL"
                    && !LocalDiscountCalculationService.IsStatutoryVatExemptCode(normalizedDiscountCode)
                    && string.IsNullOrWhiteSpace(resolvedMethod) && !resolvedValue.HasValue)
                {
                    if (localDisc != null)
                    {
                        resolvedMethod = localDisc.DiscountMethod;
                        resolvedValue = localDisc.DiscountValue;
                    }
                    else if (resolvedPromoAmount == 0m && createDto.DiscountAmount > 0m
                        && !LocalDiscountCalculationService.IsStatutoryCode(normalizedDiscountCode)
                        && normalizedDiscountCode != "EMPLOYEE")
                    {
                        // Ad-hoc / resumed held-sale promo discount validated strictly <= eligible subtotal
                        resolvedPromoAmount = createDto.DiscountAmount;
                    }
                }

                var calcReq = new OrderCalculationRequestDto
                {
                    BranchTaxType = branchTaxType,
                    DiscountCode = normalizedDiscountCode,
                    DiscountMethod = resolvedMethod,
                    DiscountValue = resolvedValue,
                    ManualDiscountPercentage = createDto.ManualDiscountPercentage,
                    ManualDiscountAmount = createDto.ManualDiscountAmount,
                    PromoDiscountAmount = resolvedPromoAmount,
                    Items = createDto.Items.Select(i => new CartItemCalculationDto
                    {
                        ProductId = i.ProductId,
                        ProductName = i.ProductName ?? "Product",
                        Quantity = i.Quantity,
                        UnitPrice = i.IsComplimentary ? 0m : i.BasePrice,
                        TargetAmount = i.IsComplimentary ? 0m : i.TargetAmount,
                        ModifierTotal = i.IsComplimentary ? 0m : (i.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m),
                        IsComplimentary = i.IsComplimentary,
                        TaxName = i.TaxName ?? "VATable",
                        IsEligibleForStatutoryDiscount = true
                    }).ToList()
                };

                var calculator = new LocalDiscountCalculationService();
                OrderCalculationResultDto calcRes;
                try
                {
                    calcRes = calculator.CalculateOrder(calcReq);
                }
                catch (ArgumentException ex)
                {
                    await transaction.RollbackAsync();
                    return new OfflineCheckoutResultDto { Success = false, ErrorMessage = ex.Message };
                }

                decimal subtotal = Math.Max(0m, calcRes.Subtotal);
                decimal totalAmount = Math.Max(0m, calcRes.TotalAmount);

                if (!hasSplits && paymentMethod == "CASH" && totalAmount > 0m && amountTendered <= 0m)
                {
                    await transaction.RollbackAsync();
                    return new OfflineCheckoutResultDto { Success = false, ErrorMessage = "A valid cash tendered amount is required for checkout." };
                }

                decimal tendered;
                decimal changeAmount;
                if (hasSplits)
                {
                    decimal splitSum = createDto.Splits!.Sum(s => s.Amount);
                    if (Math.Abs(splitSum - totalAmount) > 0.01m)
                    {
                        await transaction.RollbackAsync();
                        return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Split payment amounts (₱{splitSum:N2}) must equal total payable (₱{totalAmount:N2})." };
                    }
                    foreach (var s in createDto.Splits!)
                    {
                        bool isCashSplit = string.Equals(s.PaymentMethod?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase);
                        if (isCashSplit && s.AmountTendered > 0m && s.AmountTendered < s.Amount)
                        {
                            await transaction.RollbackAsync();
                            return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Insufficient cash tendered (₱{s.AmountTendered:N2}) for cash split (₱{s.Amount:N2})." };
                        }
                    }
                    tendered = createDto.Splits.Sum(s =>
                        string.Equals(s.PaymentMethod?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase)
                            ? (s.AmountTendered > 0m ? s.AmountTendered : s.Amount)
                            : s.Amount);
                    changeAmount = Math.Max(0m, createDto.Splits
                        .Where(s => string.Equals(s.PaymentMethod?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase))
                        .Sum(s => Math.Max(0m, (s.AmountTendered > 0m ? s.AmountTendered : s.Amount) - s.Amount)));
                }
                else
                {
                    tendered = paymentMethod == "CASH" ? amountTendered : totalAmount;
                    if (paymentMethod == "CASH" && tendered < totalAmount)
                    {
                        await transaction.RollbackAsync();
                        return new OfflineCheckoutResultDto { Success = false, ErrorMessage = $"Insufficient cash tendered. Total payable: ₱{totalAmount:N2}" };
                    }
                    changeAmount = paymentMethod == "CASH" ? tendered - totalAmount : 0m;
                }

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
                        UpdatedAt = BusinessDayClock.DeviceNow()
                    };
                    db.OrderSequences.Add(orderSeq);
                }
                orderSeq.LastIssuedNumber++;
                orderSeq.UpdatedAt = BusinessDayClock.DeviceNow();
                string orderNumber = FormatOrderNumber(outletConfig.TerminalName, createDto.TerminalId, orderSeq.LastIssuedNumber);

                // 4. Persist Local Order (Capture exact device-local wall-clock timestamp, UTC offset, and timezone ID at transaction completion)
                DateTimeOffset occurredAtOffset = createDto.TransactionTimestamp ?? BusinessDayClock.DeviceNowOffset();
                DateTime occurredAtLocal = DateTime.SpecifyKind(occurredAtOffset.DateTime, DateTimeKind.Unspecified);
                string deviceTimeZoneId = !string.IsNullOrWhiteSpace(createDto.DeviceTimeZoneId)
                    ? createDto.DeviceTimeZoneId!
                    : BusinessDayClock.GetDeviceTimeZoneId();
                int deviceUtcOffsetMinutes = createDto.DeviceUtcOffsetMinutes ?? (int)Math.Round(occurredAtOffset.Offset.TotalMinutes);

                createDto.TransactionTimestamp = occurredAtOffset;
                createDto.DeviceTimeZoneId = deviceTimeZoneId;
                createDto.DeviceUtcOffsetMinutes = deviceUtcOffsetMinutes;

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
                    CustomerCardNumber = createDto.CustomerCardNumber,
                    CustomerName = createDto.CustomerName,
                    CustomerAddress = createDto.CustomerAddress,
                    CustomerTin = createDto.CustomerTin,
                    CustomerBusinessStyle = createDto.CustomerBusinessStyle,
                    VatableSales = calcRes.VatableSales,
                    VatExemptSales = calcRes.VatExemptSales,
                    ZeroRatedSales = calcRes.ZeroRatedSales,
                    VatAmount = calcRes.VatAmount,
                    TotalAmount = totalAmount,
                    PaymentStatus = "PAID",
                    OrderStatus = "COMPLETED",
                    CreatedAt = occurredAtLocal,
                    DeviceTimeZoneId = deviceTimeZoneId,
                    DeviceUtcOffsetMinutes = deviceUtcOffsetMinutes
                };

                db.Orders.Add(localOrder);

                // 5. Persist Order Items (matched by index to preserve exact decimal totals across duplicate ProductIds)
                var receiptItems = new List<OrderItemResponseDto>();
                for (int idx = 0; idx < createDto.Items.Count; idx++)
                {
                    var itemDto = createDto.Items[idx];
                    decimal modifierTotal = itemDto.IsComplimentary ? 0m : (itemDto.Modifiers?.Sum(m => m.AdditionalPrice) ?? 0m);
                    decimal itemEffPrice = itemDto.IsComplimentary ? 0m : (itemDto.BasePrice + modifierTotal);
                    var calcItem = idx < calcRes.CalculatedItems.Count
                        ? calcRes.CalculatedItems[idx]
                        : calcRes.CalculatedItems.FirstOrDefault(ci => ci.ProductId == itemDto.ProductId);
                    decimal lineTotal = itemDto.IsComplimentary
                        ? 0m
                        : (itemDto.TargetAmount.HasValue && itemDto.TargetAmount.Value > 0
                            ? itemDto.TargetAmount.Value
                            : (calcItem != null ? calcItem.LineTotal : Math.Round(itemEffPrice * itemDto.Quantity, 2, MidpointRounding.AwayFromZero)));

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
                        Quantity = itemDto.Quantity,
                        UnitPrice = itemEffPrice,
                        LineTotal = lineTotal,
                        PricingMethod = itemDto.PricingMethod,
                        TargetAmount = itemDto.TargetAmount,
                        IsComplimentary = itemDto.IsComplimentary,
                        ParentProductId = itemDto.ParentProductId,
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
                        BasePrice = itemDto.IsComplimentary ? 0m : itemDto.BasePrice,
                        ModifierTotal = modifierTotal,
                        UnitPrice = itemEffPrice,
                        LineTotal = lineTotal,
                        PricingMethod = itemDto.PricingMethod,
                        TargetAmount = itemDto.TargetAmount,
                        IsComplimentary = itemDto.IsComplimentary,
                        ParentProductId = itemDto.ParentProductId,
                        UnitOfMeasure = !string.IsNullOrWhiteSpace(itemDto.UnitOfMeasure) ? itemDto.UnitOfMeasure : localProd?.UnitOfMeasure,
                        VatType = calcItem?.VatType ?? "VATABLE",
                        VatAmount = calcItem?.VatAmount ?? 0m,
                        IsKitchenItem = isKitchen,
                        IsBarItem = isBar,
                        SpecialInstructions = normalizedInstructions,
                        Modifiers = MapModifierResponseDtos(itemDto.Modifiers)
                    });
                }

                // 6. Persist Payment(s) using the exact device-local transaction occurrence timestamp
                var paidAt = occurredAtLocal;
                var receiptPayments = new List<PaymentResponseDto>();
                decimal cashPortionTotal = 0m;
                if (hasSplits)
                {
                    foreach (var split in createDto.Splits!)
                    {
                        string splitMethod = string.IsNullOrWhiteSpace(split.PaymentMethod) ? "CASH" : split.PaymentMethod.Trim().ToUpperInvariant();
                        bool isCashSplit = splitMethod == "CASH";
                        decimal splitTendered = isCashSplit ? (split.AmountTendered > 0m ? split.AmountTendered : split.Amount) : split.Amount;
                        decimal splitChange = isCashSplit ? Math.Max(0m, splitTendered - split.Amount) : 0m;
                        string? splitRef = isCashSplit || string.IsNullOrWhiteSpace(split.ReferenceNumber) ? null : split.ReferenceNumber.Trim();
                        split.PaymentMethod = splitMethod;
                        split.AmountTendered = splitTendered;
                        split.ReferenceNumber = splitRef;
                        db.Payments.Add(new LocalPayment
                        {
                            OrderId = localTransactionId,
                            Amount = split.Amount,
                            TenderedAmount = splitTendered,
                            ChangeAmount = splitChange,
                            PaymentMethod = splitMethod,
                            PaymentStatus = "PAID",
                            ReferenceNumber = splitRef,
                            PaidAt = paidAt
                        });
                        if (splitMethod == "CASH") cashPortionTotal += split.Amount;
                        receiptPayments.Add(new PaymentResponseDto
                        {
                            PaymentMethod = splitMethod,
                            Amount = split.Amount,
                            AmountTendered = splitTendered,
                            ChangeAmount = splitChange,
                            ReferenceNumber = splitRef,
                            PaymentStatus = "PAID",
                            ProcessedBy = cashierId,
                            PaidAt = paidAt
                        });
                    }
                }
                else
                {
                    string? cleanRef = string.IsNullOrWhiteSpace(referenceNumber) ? null : referenceNumber.Trim();
                    db.Payments.Add(new LocalPayment
                    {
                        OrderId = localTransactionId,
                        Amount = totalAmount,
                        TenderedAmount = tendered,
                        ChangeAmount = changeAmount,
                        PaymentMethod = paymentMethod,
                        PaymentStatus = "PAID",
                        ReferenceNumber = cleanRef,
                        PaidAt = paidAt
                    });
                    if (paymentMethod == "CASH") cashPortionTotal = totalAmount;
                    receiptPayments.Add(new PaymentResponseDto
                    {
                        PaymentMethod = paymentMethod,
                        Amount = totalAmount,
                        AmountTendered = tendered,
                        ChangeAmount = changeAmount,
                        ReferenceNumber = cleanRef,
                        PaymentStatus = "PAID",
                        ProcessedBy = cashierId,
                        PaidAt = paidAt
                    });
                }

                // 6b. Update active LocalCashDrawerShift so offline ExpectedBalance reflects cash sales immediately
                var localOpenShift = confirmedShift;
                if (localOpenShift != null && cashPortionTotal > 0m)
                {
                    localOpenShift.TotalCashSales += cashPortionTotal;
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
                LocalDatabaseBackupService.TriggerBackgroundSurvivableBackup();

                var sessionOutlet = _session.Terminal;
                string branchName = !string.IsNullOrWhiteSpace(localBranch?.Name)
                    ? localBranch.Name
                    : (!string.IsNullOrWhiteSpace(outletConfig?.BranchName)
                        ? outletConfig.BranchName!
                        : (!string.IsNullOrWhiteSpace(sessionOutlet?.BranchName) ? sessionOutlet.BranchName : (outletConfig?.TerminalName ?? sessionOutlet?.TerminalName ?? string.Empty)));
                string? branchTagline = localBranch != null
                    ? (string.IsNullOrWhiteSpace(localBranch.BranchTagline) ? null : localBranch.BranchTagline.Trim())
                    : (!string.IsNullOrWhiteSpace(outletConfig?.BranchTagline)
                        ? outletConfig.BranchTagline.Trim()
                        : (string.IsNullOrWhiteSpace(sessionOutlet?.BranchTagline) ? null : sessionOutlet.BranchTagline.Trim()));
                string? branchCode = !string.IsNullOrWhiteSpace(localBranch?.Code)
                    ? localBranch.Code
                    : (!string.IsNullOrWhiteSpace(outletConfig?.BranchCode)
                        ? outletConfig.BranchCode
                        : sessionOutlet?.BranchCode);
                string? branchAddress = !string.IsNullOrWhiteSpace(localBranch?.Address)
                    ? localBranch.Address
                    : (!string.IsNullOrWhiteSpace(outletConfig?.BranchAddress)
                        ? outletConfig.BranchAddress
                        : sessionOutlet?.BranchAddress);
                string? branchPhone = !string.IsNullOrWhiteSpace(localBranch?.Phone)
                    ? localBranch.Phone
                    : (!string.IsNullOrWhiteSpace(outletConfig?.BranchPhone)
                        ? outletConfig.BranchPhone
                        : sessionOutlet?.BranchPhone);
                string? receiptLogoUrl = !string.IsNullOrWhiteSpace(localBranch?.ReceiptLogoUrl)
                    ? localBranch.ReceiptLogoUrl
                    : (!string.IsNullOrWhiteSpace(outletConfig?.ReceiptLogoUrl)
                        ? outletConfig.ReceiptLogoUrl
                        : sessionOutlet?.ReceiptLogoUrl);
                string? receiptLogoDataUri = !string.IsNullOrWhiteSpace(localBranch?.ReceiptLogoUrl) && !string.IsNullOrWhiteSpace(localBranch.ReceiptLogoDataUri)
                    ? localBranch.ReceiptLogoDataUri
                    : (!string.IsNullOrWhiteSpace(outletConfig?.ReceiptLogoUrl) && !string.IsNullOrWhiteSpace(outletConfig.ReceiptLogoDataUri)
                        ? outletConfig.ReceiptLogoDataUri
                        : (!string.IsNullOrWhiteSpace(sessionOutlet?.ReceiptLogoUrl) ? sessionOutlet.ReceiptLogoDataUri : null));
                bool vatDisplay = localBranch?.VatDisplay ?? outletConfig?.VatDisplay ?? sessionOutlet?.VatDisplay ?? true;
                bool showCustomerDetails = localBranch?.ShowCustomerDetailsOnReceipt ?? outletConfig?.ShowCustomerDetailsOnReceipt ?? sessionOutlet?.ShowCustomerDetailsOnReceipt ?? true;
                string terminalName = !string.IsNullOrWhiteSpace(outletConfig?.TerminalName)
                    ? outletConfig.TerminalName
                    : (sessionOutlet?.TerminalName ?? string.Empty);

                var offlineReceipt = new ReceiptDto
                {
                    OrderId = 0,
                    OrderNumber = orderNumber,
                    BranchId = createDto.BranchId,
                    BranchName = branchName,
                    BranchTagline = branchTagline,
                    BranchCode = branchCode,
                    BranchAddress = branchAddress,
                    BranchPhone = branchPhone,
                    ReceiptLogoUrl = receiptLogoUrl,
                    ReceiptLogoDataUri = receiptLogoDataUri,
                    VatDisplay = vatDisplay,
                    ShowCustomerDetailsOnReceipt = showCustomerDetails,
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
                    DeviceTimeZoneId = deviceTimeZoneId,
                    DeviceUtcOffsetMinutes = deviceUtcOffsetMinutes,
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
                    Payments = receiptPayments
                };

                return new OfflineCheckoutResultDto
                {
                    Success = true,
                    LocalTransactionId = localTransactionId,
                    InvoiceNumber = invoiceNumber,
                    ChangeAmount = changeAmount,
                    CashAmountPaid = cashPortionTotal,
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

    public async Task<ReceiptDto?> GetSavedReceiptByTransactionIdAsync(string localTransactionId, CancellationToken cancellationToken = default)
    {
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        return await LoadSavedReceiptFromDbAsync(db, localTransactionId, _session, cancellationToken);
    }

    /// <summary>
    /// Verifies that the transaction is persisted in local SQLite as PAID + COMPLETED and reconstructs
    /// the complete ReceiptDto directly from committed SQLite tables for offline-first automatic printing.
    /// </summary>
    public static async Task<ReceiptDto?> LoadSavedReceiptFromDbAsync(
        LocalPosDbContext db,
        string localTransactionId,
        PosSessionService? session = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(localTransactionId)) return null;

        var order = await db.Orders.AsNoTracking()
            .FirstOrDefaultAsync(o => o.OrderId == localTransactionId, cancellationToken);
        if (order == null
            || !string.Equals(order.PaymentStatus?.Trim(), POS.Shared.Constants.PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(order.OrderStatus?.Trim(), "COMPLETED", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.OrderId == order.OrderId)
            .OrderBy(p => p.PaymentId)
            .ToListAsync(cancellationToken);
        if (payments.Count == 0
            || payments.Any(p => !string.Equals(p.PaymentStatus?.Trim(), POS.Shared.Constants.PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var orderItems = await db.OrderItems.AsNoTracking()
            .Where(i => i.OrderId == order.OrderId)
            .OrderBy(i => i.OrderItemId)
            .ToListAsync(cancellationToken);
        if (orderItems.Count == 0) return null;

        var localBranch = order.BranchId > 0
            ? await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == order.BranchId, cancellationToken)
            : null;
        var outletConfig = order.TerminalId > 0
            ? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == order.TerminalId, cancellationToken)
            : null;
        var sessionOutlet = session?.Terminal != null
            && (session.Terminal.TerminalId == order.TerminalId || session.Terminal.BranchId == order.BranchId)
            ? session.Terminal
            : null;

        string branchName = !string.IsNullOrWhiteSpace(localBranch?.Name)
            ? localBranch.Name
            : (!string.IsNullOrWhiteSpace(outletConfig?.BranchName)
                ? outletConfig.BranchName!
                : (!string.IsNullOrWhiteSpace(sessionOutlet?.BranchName)
                    ? sessionOutlet.BranchName
                    : (outletConfig?.TerminalName ?? sessionOutlet?.TerminalName ?? string.Empty)));
        string? branchTagline = localBranch != null
            ? (string.IsNullOrWhiteSpace(localBranch.BranchTagline) ? null : localBranch.BranchTagline.Trim())
            : (!string.IsNullOrWhiteSpace(outletConfig?.BranchTagline)
                ? outletConfig.BranchTagline.Trim()
                : (string.IsNullOrWhiteSpace(sessionOutlet?.BranchTagline) ? null : sessionOutlet.BranchTagline.Trim()));
        string? branchCode = !string.IsNullOrWhiteSpace(localBranch?.Code)
            ? localBranch.Code
            : (!string.IsNullOrWhiteSpace(outletConfig?.BranchCode) ? outletConfig.BranchCode : sessionOutlet?.BranchCode);
        string? branchAddress = !string.IsNullOrWhiteSpace(localBranch?.Address)
            ? localBranch.Address
            : (!string.IsNullOrWhiteSpace(outletConfig?.BranchAddress) ? outletConfig.BranchAddress : sessionOutlet?.BranchAddress);
        string? branchPhone = !string.IsNullOrWhiteSpace(localBranch?.Phone)
            ? localBranch.Phone
            : (!string.IsNullOrWhiteSpace(outletConfig?.BranchPhone) ? outletConfig.BranchPhone : sessionOutlet?.BranchPhone);
        string? receiptLogoUrl = !string.IsNullOrWhiteSpace(localBranch?.ReceiptLogoUrl)
            ? localBranch.ReceiptLogoUrl
            : (!string.IsNullOrWhiteSpace(outletConfig?.ReceiptLogoUrl) ? outletConfig.ReceiptLogoUrl : sessionOutlet?.ReceiptLogoUrl);
        string? receiptLogoDataUri = !string.IsNullOrWhiteSpace(localBranch?.ReceiptLogoUrl) && !string.IsNullOrWhiteSpace(localBranch.ReceiptLogoDataUri)
            ? localBranch.ReceiptLogoDataUri
            : (!string.IsNullOrWhiteSpace(outletConfig?.ReceiptLogoUrl) && !string.IsNullOrWhiteSpace(outletConfig.ReceiptLogoDataUri)
                ? outletConfig.ReceiptLogoDataUri
                : (!string.IsNullOrWhiteSpace(sessionOutlet?.ReceiptLogoUrl) ? sessionOutlet.ReceiptLogoDataUri : null));
        bool vatDisplay = localBranch?.VatDisplay ?? outletConfig?.VatDisplay ?? sessionOutlet?.VatDisplay ?? true;
        bool showCustomerDetails = localBranch?.ShowCustomerDetailsOnReceipt ?? outletConfig?.ShowCustomerDetailsOnReceipt ?? sessionOutlet?.ShowCustomerDetailsOnReceipt ?? true;
        string terminalName = !string.IsNullOrWhiteSpace(outletConfig?.TerminalName)
            ? outletConfig.TerminalName
            : (sessionOutlet?.TerminalName ?? $"Terminal #{order.TerminalId}");

        var productMetadata = await LoadLocalProductMetadataAsync(
            db,
            orderItems.Select(i => i.ProductId),
            cancellationToken);
        var receiptItems = MapLocalOrderItems(orderItems, productMetadata);

        var paidAt = payments.Max(p => p.PaidAt);
        if (paidAt == default) paidAt = order.CreatedAt;

        var receipt = new ReceiptDto
        {
            OrderId = 0,
            OrderNumber = order.OrderNumber ?? order.InvoiceNumber,
            BranchId = order.BranchId,
            BranchName = branchName,
            BranchTagline = branchTagline,
            BranchCode = branchCode,
            BranchAddress = branchAddress,
            BranchPhone = branchPhone,
            ReceiptLogoUrl = receiptLogoUrl,
            ReceiptLogoDataUri = receiptLogoDataUri,
            VatDisplay = vatDisplay,
            ShowCustomerDetailsOnReceipt = showCustomerDetails,
            TerminalId = order.TerminalId,
            OutletId = outletConfig?.OutletId ?? order.OutletId,
            OutletName = outletConfig?.OutletName ?? sessionOutlet?.OutletName ?? string.Empty,
            PTUNumber = outletConfig?.PTUNumber ?? sessionOutlet?.PTUNumber,
            MINNumber = outletConfig?.MINNumber ?? sessionOutlet?.MINNumber,
            SerialNumber = outletConfig?.SerialNumber ?? sessionOutlet?.SerialNumber,
            TerminalName = terminalName,
            CashierId = order.CashierId,
            OrderType = order.OrderType,
            TableNumber = order.TableNumber,
            HeadCount = order.HeadCount,
            CreatedAt = order.CreatedAt,
            PaidAt = paidAt,
            ServerReceivedAtUtc = order.ServerReceivedAtUtc,
            DeviceTimeZoneId = order.DeviceTimeZoneId,
            DeviceUtcOffsetMinutes = order.DeviceUtcOffsetMinutes,
            Subtotal = order.Subtotal,
            VatableSales = order.VatableSales,
            VatExemptSales = order.VatExemptSales,
            ZeroRatedSales = order.ZeroRatedSales,
            VatAmount = order.VatAmount,
            DiscountCode = order.DiscountCode,
            CustomerCardNumber = order.CustomerCardNumber,
            DiscountAmount = order.DiscountAmount,
            TotalAmount = order.TotalAmount,
            InvoiceNumber = order.InvoiceNumber,
            InvoiceIssuedAt = paidAt,
            CustomerName = order.CustomerName,
            CustomerAddress = order.CustomerAddress,
            CustomerTin = order.CustomerTin,
            CustomerBusinessStyle = order.CustomerBusinessStyle,
            Payments = payments.Select(p => new PaymentResponseDto
            {
                PaymentId = p.PaymentId,
                OrderId = 0,
                PaymentMethod = p.PaymentMethod,
                Amount = p.Amount,
                AmountTendered = p.TenderedAmount,
                ChangeAmount = p.ChangeAmount,
                ReferenceNumber = p.ReferenceNumber,
                PaymentStatus = p.PaymentStatus,
                ProcessedBy = order.CashierId,
                PaidAt = p.PaidAt
            }).ToList(),
            Items = receiptItems
        };

        PosSessionService.EnsureReceiptLogoPopulated(receipt, session);
        return receipt;
    }

    internal sealed record LocalProductMetadata(string? UnitOfMeasure, string? PricingMethod);

    /// <summary>
    /// Batch-loads current unit-of-measure and pricing-method metadata for local products referenced by order items.
    /// </summary>
    internal static async Task<Dictionary<int, LocalProductMetadata>> LoadLocalProductMetadataAsync(
        LocalPosDbContext db,
        IEnumerable<int> productIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (productIds is null) return new Dictionary<int, LocalProductMetadata>();

        var distinctIds = productIds.Distinct().ToList();
        if (distinctIds.Count == 0)
            return new Dictionary<int, LocalProductMetadata>();

        return await db.Products.AsNoTracking()
            .Where(p => distinctIds.Contains(p.ProductId))
            .Select(p => new { p.ProductId, p.UnitOfMeasure, p.PricingMethod })
            .ToDictionaryAsync(
                p => p.ProductId,
                p => new LocalProductMetadata(p.UnitOfMeasure, p.PricingMethod),
                cancellationToken);
    }

    /// <summary>
    /// Maps persisted local SQLite order items and batch-loaded product metadata into receipt/transaction DTOs.
    /// </summary>
    internal static List<OrderItemResponseDto> MapLocalOrderItems(
        IEnumerable<LocalOrderItem> orderItems,
        IReadOnlyDictionary<int, LocalProductMetadata>? productsById = null)
    {
        if (orderItems is null) return new List<OrderItemResponseDto>();

        var items = orderItems as ICollection<LocalOrderItem> ?? orderItems.ToList();
        var result = new List<OrderItemResponseDto>(items.Count);
        foreach (var item in items)
        {
            var prod = productsById != null && productsById.TryGetValue(item.ProductId, out var foundProd)
                ? foundProd
                : null;

            var mods = DeserializeModifiers(item.ModifiersJson);
            decimal modifierTotal = item.IsComplimentary ? 0m : mods.Sum(m => m.AdditionalPrice);
            decimal basePrice = item.IsComplimentary ? 0m : Math.Max(0m, item.UnitPrice - modifierTotal);
            string vatType = ResolveVatType(item.TaxName);

            result.Add(new OrderItemResponseDto
            {
                OrderItemId = item.OrderItemId,
                ProductId = item.ProductId,
                ProductName = item.ProductName,
                ProductShortCode = item.ProductShortCode,
                Quantity = item.Quantity,
                BasePrice = basePrice,
                ModifierTotal = modifierTotal,
                UnitPrice = item.UnitPrice,
                LineTotal = item.LineTotal,
                PricingMethod = !string.IsNullOrWhiteSpace(item.PricingMethod)
                    ? item.PricingMethod
                    : (!item.IsComplimentary ? prod?.PricingMethod : null),
                TargetAmount = item.TargetAmount,
                IsComplimentary = item.IsComplimentary,
                ParentProductId = item.ParentProductId,
                UnitOfMeasure = prod?.UnitOfMeasure,
                VatType = vatType,
                VatAmount = 0m,
                IsKitchenItem = item.IsKitchenItem,
                IsBarItem = item.IsBarItem,
                SpecialInstructions = item.SpecialInstructions,
                Modifiers = mods
            });
        }

        return result;
    }

    private static List<OrderItemModifierResponseDto> DeserializeModifiers(string? modifiersJson)
    {
        if (string.IsNullOrWhiteSpace(modifiersJson)) return new();
        try
        {
            var raw = System.Text.Json.JsonSerializer.Deserialize<List<OrderItemModifierCreateDto>>(modifiersJson);
            return MapModifierResponseDtos(raw);
        }
        catch
        {
            return new();
        }
    }

    private static List<OrderItemModifierResponseDto> MapModifierResponseDtos(IReadOnlyCollection<OrderItemModifierCreateDto>? modifiers)
    {
        if (modifiers is null || modifiers.Count == 0)
            return new();

        return modifiers.Select((m, idx) => new OrderItemModifierResponseDto
        {
            OrderItemModifierId = idx + 1,
            ModifierOptionId = m.ModifierOptionId,
            ProductId = m.ProductId,
            ModifierName = m.ModifierName,
            AdditionalPrice = m.AdditionalPrice
        }).ToList();
    }

    private static string ResolveVatType(string? taxName)
    {
        if (string.IsNullOrWhiteSpace(taxName)) return "VATABLE";
        var upper = taxName.Trim().ToUpperInvariant();
        if (upper.Contains("EXEMPT", StringComparison.Ordinal)) return "VAT_EXEMPT";
        if (upper.Contains("ZERO", StringComparison.Ordinal)) return "ZERO_RATED";
        return "VATABLE";
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
