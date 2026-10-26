using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace POS.Client.Data;

public class LocalProduct
{
    [Key]
    public int ProductId { get; set; }

    [ForeignKey("Category")]
    public int CategoryId { get; set; }
    public virtual LocalCategory Category { get; set; } = null!;
    public string CategoryName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal? UnitValue { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? Barcode { get; set; }
    public string? SKU { get; set; }
    public string? ImageUrl { get; set; }

    public decimal Price { get; set; }
    public decimal CostPrice { get; set; }

    public string? ShortCode { get; set; }

    [ForeignKey("Tax")]
    public int? TaxId { get; set; }
    public virtual LocalTax? Tax { get; set; }
    public string? TaxName { get; set; } = "VATable";
    public string ProductType { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public bool IsKitchenItem { get; set; }
    public bool IsBarItem { get; set; }
}

public class LocalCategory
{
    [Key]
    public int CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string? ColorCode { get; set; }
}

public class LocalProductModifierGroup
{
    [Key]
    public int ProductModifierGroupId { get; set; }
    public int ProductId { get; set; }
    public int ModifierGroupId { get; set; }
    public int DisplayOrder { get; set; }

    public virtual LocalProduct LocalProduct { get; set; } = null!;
    public virtual LocalModifierGroup ModifierGroup { get; set; } = null!;
}

public class LocalModifierGroup
{
    [Key]
    public int ModifierGroupId { get; set; }
    public string Name { get; set; } = null!;
    public bool IsRequired { get; set; }
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }

    public virtual ICollection<LocalModifierOption> LocalModifierOption { get; set; } = new List<LocalModifierOption>();
    public virtual ICollection<LocalProductModifierGroup> LocalProductModifierGroup { get; set; } = new List<LocalProductModifierGroup>();
}

public class LocalModifierOption
{
    [Key]
    public int ModifierOptionId { get; set; }
    public int ModifierGroupId { get; set; }
    public string Name { get; set; } = null!;
    public decimal Price { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }

    public virtual LocalModifierGroup LocalModifierGroup { get; set; } = null!;
}

public class LocalTax
{
    [Key]
    public int TaxId { get; set; }
    public string Name { get; set; } = null!;
    [Column(TypeName = "decimal(18,4)")]
    public decimal Rate { get; set; }
}

public class LocalDiscount
{
    [Key]
    public int DiscountId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string DiscountCategory { get; set; } = null!;
    public string DiscountMethod { get; set; } = null!;
    [Column(TypeName = "decimal(18,2)")]
    public decimal DiscountValue { get; set; }
    public bool IsStatutory { get; set; }
    public bool RequiresIdentification { get; set; }
}

public class LocalPaymentMethod
{
    [Key]
    public int PaymentMethodId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool RequiresReference { get; set; }
    public bool IsActive { get; set; } = true;
    public int DisplayOrder { get; set; }
}

public class LocalTerminalConfig
{
    [Key]
    public int TerminalId { get; set; }
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public string TerminalCode { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public string? PTUNumber { get; set; }
    public string? MINNumber { get; set; }
    public string? SerialNumber { get; set; }
    public int BranchId { get; set; }
    public int? BusinessTypeId { get; set; }
    public string? BusinessType { get; set; }
    public string? BranchName { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string TerminalName { get; set; } = null!;
    public string BranchTaxType { get; set; } = "VAT-Registered";
    public string TerminalIdentifier { get; set; } = null!;
    public string? ReceiptPrinterName { get; set; }
    public string? KitchenPrinterName { get; set; }
    public string? BarPrinterName { get; set; }
}

public class LocalInvoiceSequence
{
    [Key]
    public int SequenceId { get; set; }
    public int TerminalId { get; set; }
    public string SeriesCode { get; set; } = null!;
    public string Prefix { get; set; } = null!;
    public long StartingNumber { get; set; }
    public long EndingNumber { get; set; }
    public long LastIssuedNumber { get; set; }
    public int PaddingLength { get; set; } = 7;
    public bool IsActive { get; set; } = true;
}

public class LocalOrder
{
    [Key]
    public string OrderId { get; set; } = Guid.NewGuid().ToString();
    public string InvoiceNumber { get; set; } = null!;
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public int TerminalId { get; set; }
    public long? ShiftId { get; set; }
    public string? ClientShiftId { get; set; }
    public string OrderType { get; set; } = "DINING";
    public string? TableNumber { get; set; }
    public int? HeadCount { get; set; }
    public string CashierId { get; set; } = null!;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Subtotal { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal DiscountAmount { get; set; }
    public string? DiscountCode { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal VatableSales { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal VatExemptSales { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal ZeroRatedSales { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal VatAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalAmount { get; set; }
    public string PaymentStatus { get; set; } = "PENDING";
    public string OrderStatus { get; set; } = "COMPLETED";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LocalOrderItem
{
    [Key]
    public long OrderItemId { get; set; }
    public string OrderId { get; set; } = null!;
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public int Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }
    public string TaxName { get; set; } = "VATable";
}

public class LocalPayment
{
    [Key]
    public long PaymentId { get; set; }
    public string OrderId { get; set; } = null!;
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TenderedAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal ChangeAmount { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public string PaymentStatus { get; set; } = "SUCCESS";
    public DateTime PaidAt { get; set; } = DateTime.UtcNow;
}

public class LocalSyncOutbox
{
    [Key]
    public long OutboxId { get; set; }
    public string EntityType { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public string PayloadJson { get; set; } = null!;
    public string Status { get; set; } = "PENDING"; // PENDING, SYNCED, FAILED, RETRY_PENDING, NEEDS_ATTENTION
    public int RetryCount { get; set; } = 0;
    public string? LastError { get; set; }
    public DateTime? NextRetryAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class LocalSyncAuditLog
{
    [Key]
    public long LogId { get; set; }
    public string Message { get; set; } = null!;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class LocalHeldSale
{
    [Key]
    public long HeldSaleId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public string HoldReference { get; set; } = string.Empty;
    public string Status { get; set; } = "HELD";
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResumedAt { get; set; }
}

public class LocalCashier
{
    [Key]
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
}

public class LocalCashMovementCategory
{
    [Key]
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string MovementType { get; set; } = "PETTY_CASH";
    public bool RequiresApproval { get; set; } = true;
    [Column(TypeName = "decimal(18,2)")]
    public decimal ApprovalThreshold { get; set; }
    public bool IsActive { get; set; } = true;
}

public class LocalCashDrawerShift
{
    public bool OpeningConfirmed { get; set; }
    public string? ClientShiftId { get; set; }
    public string? ClientCloseId { get; set; }
    public string? ClosingDenominationsJson { get; set; }
    public string? ClosingOriginalDeclarationJson { get; set; }
    public string? ClosingPaymentReconciliationsJson { get; set; }
    public string? ClosingRecountHistoryJson { get; set; }
    [Key]
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string CashierId { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningFloat { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCashSales { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCashRefunds { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCashIn { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCashOut { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCashDrops { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal ExpectedBalance { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ActualCountedBalance { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Discrepancy { get; set; }
    public string Status { get; set; } = "OPEN";
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public string? Notes { get; set; }
}

public class LocalCashDrawerMovement
{
    [Key]
    public long MovementId { get; set; }
    public string ClientMovementId { get; set; } = Guid.NewGuid().ToString("N");
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string MovementType { get; set; } = "PETTY_CASH";
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "POSTED"; // PENDING_APPROVAL, POSTED, REJECTED, REVERSED
    public string CreatedBy { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public string? SupervisorPinHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    public string SyncStatus { get; set; } = "PENDING"; // PENDING, SYNCED, NEEDS_ATTENTION, CONFLICT
    public string? SyncError { get; set; }
}
