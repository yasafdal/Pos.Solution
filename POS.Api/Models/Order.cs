using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class Order
{
    public long OrderId { get; set; }

    [Required]
    [MaxLength(30)]
    public string OrderNumber { get; set; } = null!;

    public int BranchId { get; set; }

    public int OutletId { get; set; }

    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;

    public long? ShiftId { get; set; }

    [Required]
    [MaxLength(20)]
    public string OrderType { get; set; } = null!;

    [Required]
    [MaxLength(20)]
    public string OrderStatus { get; set; } = null!;

    [Required]
    [MaxLength(20)]
    public string PaymentStatus { get; set; } = null!;

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal TotalAmount { get; set; }

    public int? InvoiceSequenceId { get; set; }

    [MaxLength(50)]
    public string? InvoiceNumber { get; set; }

    public DateTime? InvoiceIssuedAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual ICollection<Payment> Payments { get; set; } = new List<Payment>();

    public virtual InvoiceSequence? InvoiceSequence { get; set; }
}
