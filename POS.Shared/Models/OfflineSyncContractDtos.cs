using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class OfflineTransactionSyncRequest
{
    public long? ShiftId { get; set; }
    public string? ClientShiftId { get; set; }
    public string LocalTransactionId { get; set; } = null!;
    public string DeviceIdentifier { get; set; } = null!;
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string CashierId { get; set; } = null!;
    public string InvoiceNumber { get; set; } = null!;
    public string? OrderType { get; set; }
    public string? TableNumber { get; set; }
    public int? HeadCount { get; set; }
    public DateTime TransactionDate { get; set; }
    public string SchemaVersion { get; set; } = "1.0";
    public string FinancialCalculationVersion { get; set; } = "1.0";
    public string ReferenceDataVersion { get; set; } = null!;
    public string IdempotencyKey { get; set; } = null!;
    public string PayloadHash { get; set; } = null!;

    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountCode { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }
    public decimal TotalAmount { get; set; }

    public List<OfflineTransactionItemDto> Items { get; set; } = new();
    public List<OfflinePaymentDto> Payments { get; set; } = new();
}

public class OfflineTransactionItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string TaxName { get; set; } = "VATable";
    public List<OfflineModifierDto> Modifiers { get; set; } = new();
}

public class OfflineModifierDto
{
    public int OptionId { get; set; }
    public string Name { get; set; } = null!;
    public decimal AdditionalPrice { get; set; }
}

public class OfflinePaymentDto
{
    public decimal Amount { get; set; }
    public decimal TenderedAmount { get; set; }
    public decimal ChangeAmount { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public string PaymentStatus { get; set; } = "SUCCESS";
    public DateTime PaidAt { get; set; }
}

public class OfflineTransactionSyncResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string LocalTransactionId { get; set; } = null!;
    public string InvoiceNumber { get; set; } = null!;
    public long ServerOrderId { get; set; }
    public DateTime AcknowledgedAt { get; set; }
}

public class OfflineTransactionStatusDto
{
    public string Status { get; set; } = string.Empty;
    public string LocalTransactionId { get; set; } = string.Empty;
    public string InvoiceNumber { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public long ServerOrderId { get; set; }
}

public class OfflineCashMovementSyncRequest
{
    public string ClientMovementId { get; set; } = string.Empty;
    public long ShiftId { get; set; }
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string MovementType { get; set; } = "PETTY_CASH";
    public int? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "POSTED";
    public string CreatedBy { get; set; } = string.Empty;
    public string? ApprovedBy { get; set; }
    public string? SupervisorPinHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? PostedAt { get; set; }
}

public class OfflineCashMovementSyncResponse
{
    public bool Success { get; set; }
    public bool IsConflict { get; set; }
    public string? Message { get; set; }
    public string ClientMovementId { get; set; } = string.Empty;
    public string ReferenceNumber { get; set; } = string.Empty;
    public long ServerMovementId { get; set; }
    public string ServerStatus { get; set; } = "POSTED";
    public DateTime AcknowledgedAt { get; set; }
}

