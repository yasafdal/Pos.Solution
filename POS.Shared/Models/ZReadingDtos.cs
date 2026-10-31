namespace POS.Shared.Models;

public sealed class GenerateZReadingRequest
{
    public int TerminalId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public string ClientOperationId { get; set; } = "";
    public bool AllLocalOutboxesDrained { get; set; }
    public bool NoUnresolvedLocalTransactions { get; set; }
    public bool SoleDeviceCompletenessConfirmed { get; set; }
    public string DeviceIdentifier { get; set; } = "";
}

public sealed class ZReadingReadinessDto
{
    public int TerminalId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public bool Enabled { get; set; }
    public bool CanGenerate => IsReady;
    public bool IsReady { get; set; }
    public bool AlreadyGenerated { get; set; }
    public Guid? ExistingReportId { get; set; }
    public DateOnly? NextBusinessDate { get; set; }
    public bool RequiresClientConfirmation { get; set; } = true;
    public List<string> Blockers { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ZReadingTerminalDto
{
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string TerminalCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? DeviceIdentifier { get; set; }
}

public sealed class ZReadingReportDto
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string TerminalCode { get; set; } = "";
    public string TerminalName { get; set; } = "";
    public string BranchName { get; set; } = "";
    public string OutletName { get; set; } = "";
    public string? BusinessName { get; set; }
    public string? Address { get; set; }
    public string? Tin { get; set; }
    public string? MachineIdentificationNumber { get; set; }
    public string? SerialNumber { get; set; }
    public string? PermitToUseNumber { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string GeneratedBy { get; set; } = "";
    public string ClientOperationId { get; set; } = "";
    public long? ZNumber { get; set; }
    public long? ResetCounter { get; set; }
    public decimal PreviousAccumulatedSales { get; set; }
    public decimal PresentAccumulatedSales { get; set; }
    public decimal GrossSales { get; set; }
    public decimal Discounts { get; set; }
    public decimal Returns { get; set; }
    public decimal Voids { get; set; }
    public decimal NetSales { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }
    public decimal ReturnVatAmount { get; set; }
    public decimal VoidVatAmount { get; set; }
    public decimal NetVatAmount { get; set; }
    public decimal VatAdjustment { get; set; }
    public bool HistoricalItemVatPrecisionAvailable { get; set; }
    public int SaleCount { get; set; }
    public int ReturnCount { get; set; }
    public int VoidCount { get; set; }
    public string? FirstInvoiceNumber { get; set; }
    public string? LastInvoiceNumber { get; set; }
    public long? FirstRefundId { get; set; }
    public long? LastRefundId { get; set; }
    public List<ZReadingProductDto> Products { get; set; } = [];
    public List<ZReadingProductDto> ReturnProducts { get; set; } = [];
    public List<ZReadingProductDto> VoidProducts { get; set; } = [];
    public List<ZReadingProductDto> ExemptProducts { get; set; } = [];
    public List<ZReadingCashierDto> Cashiers { get; set; } = [];
    public List<ZReadingPaymentDto> Payments { get; set; } = [];
    public List<ZReadingDiscountDto> DiscountTotals { get; set; } = [];
    public List<ZReadingShiftDto> Shifts { get; set; } = [];
    public List<ZReadingAdjustmentDto> Adjustments { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class ZReadingProductDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = "";
    public string? ProductShortCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal Value { get; set; }
    public decimal Discounts { get; set; }
    public decimal VatAmount { get; set; }
    public string VatType { get; set; } = "";
}

public sealed class ZReadingCashierDto
{
    public string CashierId { get; set; } = "";
    public string CashierName { get; set; } = "";
    public decimal GrossSales { get; set; }
    public decimal Discounts { get; set; }
    public decimal Returns { get; set; }
    public decimal Voids { get; set; }
    public decimal NetSales { get; set; }
    public int SaleCount { get; set; }
    public int ReturnCount { get; set; }
    public int VoidCount { get; set; }
    public decimal ItemQuantity { get; set; }
    public decimal ReturnQuantity { get; set; }
    public decimal VoidQuantity { get; set; }
}

public sealed class ZReadingPaymentDto
{
    public string PaymentMethod { get; set; } = "";
    public int PaymentCount { get; set; }
    public decimal Collections { get; set; }
    public int RefundCount { get; set; }
    public decimal Refunds { get; set; }
    public decimal NetCollections { get; set; }
}

public sealed class ZReadingDiscountDto
{
    public string DiscountCode { get; set; } = "";
    public string DiscountName { get; set; } = "";
    public int Count { get; set; }
    public decimal Amount { get; set; }
}

public sealed class ZReadingShiftDto
{
    public long ShiftId { get; set; }
    public string CashierId { get; set; } = "";
    public DateTime OpenedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string Status { get; set; } = "";
    public decimal OpeningFloat { get; set; }
    public decimal CashSales { get; set; }
    public decimal CashRefunds { get; set; }
    public decimal CashIn { get; set; }
    public decimal CashOut { get; set; }
    public decimal CashDrops { get; set; }
    public decimal ExpectedBalance { get; set; }
    public decimal? ActualCountedBalance { get; set; }
    public decimal? Discrepancy { get; set; }
}

public sealed class ZReadingAdjustmentDto
{
    public string Kind { get; set; } = "";
    public long EventId { get; set; }
    public long OriginalOrderId { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public DateTime EventAtUtc { get; set; }
    public string ProcessedBy { get; set; } = "";
    public decimal Amount { get; set; }
}

public sealed class StartZReadingPrintRequest
{
    public string ClientOperationId { get; set; } = "";
    public string? PrinterName { get; set; }
    public string? Reason { get; set; }
}

public sealed class CompleteZReadingPrintRequest
{
    // ACCEPTED means accepted by the print subsystem, never proof of physical output.
    public string Status { get; set; } = "UNKNOWN";
    public string? ErrorMessage { get; set; }
}

public sealed class ZReadingPrintAttemptDto
{
    public Guid Id { get; set; }
    public Guid ZReadingId { get; set; }
    public string ClientOperationId { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public string? PrinterName { get; set; }
    public string? Reason { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime CreatedAt => StartedAt;
    public bool IsReprint { get; set; }
    public string Outcome => Status;
    public string? Detail => ErrorMessage;
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "STARTED";
    public string? ErrorMessage { get; set; }
    public string SnapshotSha256 { get; set; } = "";
    public ZReadingReportDto Report { get; set; } = new();
}
