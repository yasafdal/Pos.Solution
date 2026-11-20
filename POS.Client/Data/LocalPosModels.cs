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
    public string PricingMethod { get; set; } = "Fixed";

    public bool IsActive { get; set; }
    public bool IsKitchenItem { get; set; }
    public bool IsBarItem { get; set; }
    public int DisplayOrder { get; set; }
}

public class LocalCategory
{
    [Key]
    public int CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string? ColorCode { get; set; }
    public int DisplayOrder { get; set; }
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
    public int? ProductId { get; set; }
    public string? ProductName { get; set; }
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

public class LocalBranch
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? BranchTagline { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string TaxType { get; set; } = "VAT-Registered";
    public bool VatDisplay { get; set; } = true;
    public bool TrackInventory { get; set; }
    public int? BusinessTypeId { get; set; } = 1;
    public string? BusinessType { get; set; } = "FNB";
    public int? ServiceModeId { get; set; }
    public string? ReceiptLogoUrl { get; set; }
    public string? ReceiptLogoDataUri { get; set; }
    public DateTime? ReceiptLogoUpdatedAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
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
    public string? BranchTagline { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string? ReceiptLogoUrl { get; set; }
    public string? ReceiptLogoDataUri { get; set; }
    public DateTime? ReceiptLogoUpdatedAt { get; set; }
    public bool VatDisplay { get; set; } = true;
    public string TerminalName { get; set; } = null!;
    public string BranchTaxType { get; set; } = "VAT-Registered";
    public string TerminalIdentifier { get; set; } = null!;
    public string? ReceiptPrinterName { get; set; }
    public string? KitchenPrinterName { get; set; }
    public string? BarPrinterName { get; set; }
    public POS.Shared.Models.MenuDisplayMode MenuDisplayMode { get; set; } = POS.Shared.Models.MenuDisplayMode.TextOnly;
    public bool IsInitialized { get; set; } = false;
    public string? LastSyncVersion { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public string? DeviceUuid { get; set; }
    public bool IsActivated { get; set; } = false;
    public bool AllowSync { get; set; } = true;
    public DateTime? ActivatedAt { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? DefaultOpeningFund { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? OpeningFundOverride { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? EffectiveOpeningFund { get; set; }
    public string? OpeningFundSource { get; set; }
    public string? ClosedBusinessDate { get; set; }
    public string? ClosingLockedByCashier { get; set; }
    public DateTime? ClosingLockedAtUtc { get; set; }
    public DateTime? ClosingUnlockAtUtc { get; set; }
    public long? ClosingLockedTickCount64 { get; set; }
    public DateTime? LastKnownServerUtc { get; set; }
    public long? LastKnownServerTickCount64 { get; set; }
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

public class LocalOrderSequence
{
    [Key]
    public int TerminalId { get; set; }
    public long LastIssuedNumber { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class LocalOrder
{
    [Key]
    public string OrderId { get; set; } = Guid.NewGuid().ToString();
    public string? OrderNumber { get; set; }
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
    public string? CustomerCardNumber { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
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
    public string? DeviceTimeZoneId { get; set; }
    public int? DeviceUtcOffsetMinutes { get; set; }
    public DateTime? ServerReceivedAtUtc { get; set; }
}

public class LocalOrderItem
{
    [Key]
    public long OrderItemId { get; set; }
    public string OrderId { get; set; } = null!;
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    [Column(TypeName = "decimal(18,6)")]
    public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal UnitPrice { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal LineTotal { get; set; }
    public string? PricingMethod { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? TargetAmount { get; set; }
    public bool IsComplimentary { get; set; } = false;
    public int? ParentProductId { get; set; }
    public string TaxName { get; set; } = "VATable";
    public string? ProductShortCode { get; set; }
    public bool IsKitchenItem { get; set; }
    public bool IsBarItem { get; set; }
    [MaxLength(250)]
    public string? SpecialInstructions { get; set; }
    public string? ModifiersJson { get; set; }
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
    public string PaymentStatus { get; set; } = POS.Shared.Constants.PaymentStatuses.Paid;
    public string? ReferenceNumber { get; set; }
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
    public DateTime? SyncedAt { get; set; }
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
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long HeldSaleId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string CashierId { get; set; } = string.Empty;
    public string HoldReference { get; set; } = string.Empty;
    public string Status { get; set; } = "HELD";
    public string? BusinessDate { get; set; }
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResumedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
}

public class LocalCashier
{
    [Key]
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
    public DateTime? SyncedAtUtc { get; set; }
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
    public decimal? ExpectedOpeningFund { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal? OpeningDiscrepancy { get; set; }
    public string? OpeningDenominationsJson { get; set; }
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

public class LocalTerminalLicense
{
    [Key]
    public int TerminalId { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string LicenseStatus { get; set; } = "PENDING_ACTIVATION";
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public int LicenseVersion { get; set; } = 1;
    public int DurationValue { get; set; } = 5;
    public string DurationUnit { get; set; } = "YEARS";
    public int OriginalDurationValue { get; set; } = 5;
    public string OriginalDurationUnit { get; set; } = "YEARS";
    public int RenewalCount { get; set; }
    public DateTime? LastRenewedAtUtc { get; set; }
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string EnvironmentType { get; set; } = "Production";
    public string Signature { get; set; } = string.Empty;
    public DateTime? LastValidatedServerUtc { get; set; }
    public long? LastValidatedTickCount64 { get; set; }
    public DateTime? HighWaterMarkUtc { get; set; }
    public long? HighWaterMarkTickCount64 { get; set; }
    public string? RevocationReason { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Local unified ESC/POS thermal printer (USB & Ethernet) and RJ11/RJ12 cash drawer settings per terminal.</summary>
public class LocalPrinterHardwareConfig
{
    [Key]
    public int TerminalId { get; set; }
    public bool PrinterEnabled { get; set; }
    [MaxLength(24)]
    public string ConnectionType { get; set; } = "Ethernet"; // "Ethernet" or "USB"
    public bool CashDrawerEnabled { get; set; }
    [MaxLength(64)]
    public string? PrinterIpAddress { get; set; }
    public int PrinterPort { get; set; } = 9100;
    [MaxLength(320)]
    public string? UsbDeviceId { get; set; }
    [MaxLength(200)]
    public string? UsbDeviceName { get; set; }
    public int PaperWidthMm { get; set; } = 80; // 58 or 80
    [MaxLength(16)]
    public string PrintDensity { get; set; } = "Normal"; // Light, Normal, Dark
    [MaxLength(24)]
    public string CharacterEncoding { get; set; } = "PC437"; // PC437, PC850, WPC1252, UTF-8
    public bool AutoCutEnabled { get; set; } = true;
    public int ConnectTimeoutMs { get; set; } = 2000;
    public int KickPin { get; set; } // 0 = Pin 2 (0x00, Xprinter default), 1 = Pin 5 (0x01)
    public int PulseOnUnits { get; set; } = 25; // t1 * 2ms -> 0x19 = 50ms
    public int PulseOffUnits { get; set; } = 250; // t2 * 2ms -> 0xFA = 500ms
    [MaxLength(100)]
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Durable cash drawer and printer hardware audit log with per-transaction idempotency claim.</summary>
public class LocalCashDrawerHardwareEvent
{
    [Key]
    public long EventId { get; set; }
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    [MaxLength(64)]
    public string? TransactionId { get; set; }
    [MaxLength(64)]
    public string? InvoiceNumber { get; set; }
    [MaxLength(32)]
    public string ActionType { get; set; } = string.Empty;
    [MaxLength(100)]
    public string RequestedBy { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? AuthorizedBy { get; set; }
    [MaxLength(50)]
    public string? AuthorizedRole { get; set; }
    [MaxLength(250)]
    public string? Reason { get; set; }
    [MaxLength(80)]
    public string? TargetEndpoint { get; set; }
    [MaxLength(40)]
    public string? CommandHex { get; set; }
    [MaxLength(24)]
    public string Status { get; set; } = "CLAIMED";
    [MaxLength(400)]
    public string? ErrorMessage { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}


public static class ReceiptPrintJobStatuses
{
    public const string Pending = "PENDING";
    public const string Printing = "PRINTING";
    public const string Printed = "PRINTED";
    public const string Failed = "FAILED";
}

public class LocalReceiptPrintJob
{
    [Key]
    public long PrintJobId { get; set; }

    [MaxLength(64)]
    public string TransactionId { get; set; } = string.Empty;

    [MaxLength(64)]
    public string? OrderNumber { get; set; }

    [MaxLength(64)]
    public string? InvoiceNumber { get; set; }

    public int TerminalId { get; set; }
    public int BranchId { get; set; }

    [MaxLength(20)]
    public string ConnectionType { get; set; } = "Ethernet";

    [MaxLength(80)]
    public string? TargetEndpoint { get; set; }

    [MaxLength(20)]
    public string Status { get; set; } = ReceiptPrintJobStatuses.Pending;

    public int AttemptCount { get; set; }

    [MaxLength(400)]
    public string? ErrorMessage { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? PrintedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Durable offline transaction void audit record with cryptographic supervisor proof (never stores plaintext PIN).
/// </summary>
public class LocalTransactionVoid
{
    [Key]
    [MaxLength(100)]
    public string ClientVoidId { get; set; } = Guid.NewGuid().ToString("N");
    [MaxLength(120)]
    public string IdempotencyKey { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? LocalOrderId { get; set; }
    public long? ServerOrderId { get; set; }
    [MaxLength(64)]
    public string? OriginalInvoiceNumber { get; set; }
    [MaxLength(64)]
    public string? OriginalOrderNumber { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public int TerminalId { get; set; }
    [MaxLength(100)]
    public string DeviceIdentifier { get; set; } = string.Empty;
    public long? ShiftId { get; set; }
    [MaxLength(100)]
    public string? ClientShiftId { get; set; }
    [MaxLength(500)]
    public string VoidReason { get; set; } = string.Empty;
    [MaxLength(100)]
    public string RequestedBy { get; set; } = string.Empty;
    [MaxLength(100)]
    public string ApprovedBy { get; set; } = string.Empty;
    [MaxLength(50)]
    public string ApprovedByRole { get; set; } = string.Empty;
    [MaxLength(128)]
    public string SupervisorPinHash { get; set; } = string.Empty;
    [MaxLength(128)]
    public string AuthorizationProofHash { get; set; } = string.Empty;
    [MaxLength(100)]
    public string ReversalReference { get; set; } = string.Empty;
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalVoidedAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal CashRefundAmount { get; set; }
    [Column(TypeName = "decimal(18,2)")]
    public decimal NonCashPendingRefundAmount { get; set; }
    public string? RefundBreakdownJson { get; set; }
    [MaxLength(30)]
    public string Status { get; set; } = "APPROVED_OFFLINE";
    [MaxLength(30)]
    public string SyncStatus { get; set; } = "PENDING"; // PENDING, SYNCED, CONFLICT, NEEDS_ATTENTION
    public string? SyncError { get; set; }
    public long? ServerVoidId { get; set; }
    public DateTime VoidedAtLocal { get; set; } = DateTime.Now;
    public DateTime VoidedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Local offline mirror of cashier-level Close Cashier restriction (CashierId + TerminalId + BusinessDate).
/// Enforces restriction offline without locking the terminal for other cashiers or blocking the same cashier on another terminal.
/// </summary>
public class LocalCashierClosingRestriction
{
    [Key]
    [MaxLength(100)]
    public string ClientRestrictionId { get; set; } = Guid.NewGuid().ToString("N");
    public long? ServerRestrictionId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    [MaxLength(450)]
    public string CashierId { get; set; } = string.Empty;
    [MaxLength(10)]
    public string BusinessDate { get; set; } = string.Empty;
    public long? ShiftId { get; set; }
    [MaxLength(100)]
    public string? ClientShiftId { get; set; }
    [MaxLength(100)]
    public string? ClientCloseId { get; set; }
    public DateTime ClosedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UnlockAtUtc { get; set; }
    public long? ClosedAtTickCount64 { get; set; }
    [MaxLength(20)]
    public string Status { get; set; } = "ACTIVE"; // ACTIVE, REOPENED
    [MaxLength(450)]
    public string? ReopenedBy { get; set; }
    public DateTime? ReopenedAtUtc { get; set; }
    [MaxLength(500)]
    public string? ReopenReason { get; set; }
    [MaxLength(100)]
    public string? ClientOperationId { get; set; }
    [MaxLength(30)]
    public string SyncStatus { get; set; } = "PENDING"; // PENDING, SYNCED
}
