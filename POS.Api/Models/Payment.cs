using System;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class Payment
{
    public long PaymentId { get; set; }

    public long OrderId { get; set; }

    [Required]
    [MaxLength(30)]
    public string PaymentMethod { get; set; } = null!;

    public decimal Amount { get; set; }

    public decimal AmountTendered { get; set; }

    public decimal ChangeAmount { get; set; }

    [Required]
    [MaxLength(20)]
    public string PaymentStatus { get; set; } = null!;

    [MaxLength(100)]
    public string? ReferenceNumber { get; set; }

    [Required]
    [MaxLength(450)]
    public string ProcessedBy { get; set; } = null!;

    public DateTime PaidAt { get; set; } = DateTime.UtcNow;

    public virtual Order Order { get; set; } = null!;
}
