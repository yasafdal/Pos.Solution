using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class VoidRequestDto
{
    public long OriginalOrderId { get; set; }
    [Required]
    [MaxLength(255)]
    public string VoidReason { get; set; } = null!;
    [Required]
    [MaxLength(450)]
    public string ApprovedByPinOrUsername { get; set; } = null!;
}

public class VoidResponseDto
{
    public long VoidId { get; set; }
    public long OriginalOrderId { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public string VoidReason { get; set; } = null!;
    public string RequestedBy { get; set; } = null!;
    public string ApprovedBy { get; set; } = null!;
    public DateTime VoidedAt { get; set; }
    public string Status { get; set; } = null!;
    public string? ReversalReference { get; set; }
}

public class ReturnRequestDto
{
    public long OriginalOrderId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    [Required]
    [MaxLength(255)]
    public string ReturnReason { get; set; } = null!;
    [Required]
    public List<ReturnItemRequestDto> Items { get; set; } = new();
    [Required]
    [MaxLength(450)]
    public string ApprovedByPinOrUsername { get; set; } = null!;
    [Required]
    [MaxLength(30)]
    public string RefundMethod { get; set; } = "CASH";
    [MaxLength(100)]
    public string? RefundReferenceNumber { get; set; }
}

public class ReturnItemRequestDto
{
    public long OriginalOrderItemId { get; set; }
    public decimal Quantity { get; set; }
    [MaxLength(30)]
    public string RestockDisposition { get; set; } = "RESTOCK";
}

public class ReturnResponseDto
{
    public long ReturnId { get; set; }
    public long OriginalOrderId { get; set; }
    public string ReturnReference { get; set; } = null!;
    public string ReturnReason { get; set; } = null!;
    public string Status { get; set; } = null!;
    public string RequestedBy { get; set; } = null!;
    public string? ApprovedBy { get; set; }
    public decimal TotalRefundAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string? OriginalInvoiceNumber { get; set; }
    public List<ReturnItemResponseDto> ReturnItems { get; set; } = new();
}

public class ReturnItemResponseDto
{
    public long ReturnItemId { get; set; }
    public long OriginalOrderItemId { get; set; }
    public string ProductName { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal OriginalUnitPrice { get; set; }
    public decimal RefundAmount { get; set; }
    public string RestockDisposition { get; set; } = null!;
}
