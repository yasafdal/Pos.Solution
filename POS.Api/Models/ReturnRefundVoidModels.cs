using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class TransactionVoid
{
    [Key]
    public long VoidId { get; set; }
    public long OriginalOrderId { get; set; }
    [MaxLength(50)]
    public string? OriginalInvoiceNumber { get; set; }
    [Required]
    [MaxLength(255)]
    public string VoidReason { get; set; } = null!;
    [Required]
    [MaxLength(450)]
    public string RequestedBy { get; set; } = null!;
    [Required]
    [MaxLength(450)]
    public string ApprovedBy { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime ApprovedAt { get; set; } = DateTime.UtcNow;
    public DateTime VoidedAt { get; set; } = DateTime.UtcNow;
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "APPROVED";
    [MaxLength(50)]
    public string? ReversalReference { get; set; }

    public virtual Order? OriginalOrder { get; set; }
    public virtual ICollection<Refund> Refunds { get; set; } = new List<Refund>();
}

public partial class SalesReturn
{
    [Key]
    public long ReturnId { get; set; }
    public long OriginalOrderId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    [Required]
    [MaxLength(50)]
    public string ReturnReference { get; set; } = null!;
    [Required]
    [MaxLength(255)]
    public string ReturnReason { get; set; } = null!;
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "COMPLETED";
    [Required]
    [MaxLength(450)]
    public string RequestedBy { get; set; } = null!;
    [MaxLength(450)]
    public string? ApprovedBy { get; set; }
    public decimal TotalRefundAmount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public virtual Order? OriginalOrder { get; set; }
    public virtual ICollection<SalesReturnItem> ReturnItems { get; set; } = new List<SalesReturnItem>();
    public virtual ICollection<Refund> Refunds { get; set; } = new List<Refund>();
}

public partial class SalesReturnItem
{
    [Key]
    public long ReturnItemId { get; set; }
    public long ReturnId { get; set; }
    public long OriginalOrderItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal OriginalUnitPrice { get; set; }
    public decimal AllocatedDiscount { get; set; }
    public decimal AllocatedTax { get; set; }
    public decimal RefundAmount { get; set; }
    [Required]
    [MaxLength(30)]
    public string RestockDisposition { get; set; } = "RESTOCK";

    public virtual SalesReturn? SalesReturn { get; set; }
    public virtual OrderItem? OriginalOrderItem { get; set; }
}

public partial class Refund
{
    [Key]
    public long RefundId { get; set; }
    public long? ReturnId { get; set; }
    public long? VoidId { get; set; }
    public long OriginalPaymentId { get; set; }
    [Required]
    [MaxLength(30)]
    public string RefundMethod { get; set; } = "CASH";
    public decimal RefundAmount { get; set; }
    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "COMPLETED";
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;

    public virtual SalesReturn? SalesReturn { get; set; }
    public virtual TransactionVoid? TransactionVoid { get; set; }
    public virtual Payment? OriginalPayment { get; set; }
}
