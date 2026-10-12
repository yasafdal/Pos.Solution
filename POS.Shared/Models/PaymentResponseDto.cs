using System;

namespace POS.Shared.Models;

public class PaymentResponseDto
{
    public long PaymentId { get; set; }
    public long OrderId { get; set; }
    public string PaymentMethod { get; set; } = null!;
    public decimal Amount { get; set; }
    public decimal AmountTendered { get; set; }
    public decimal ChangeAmount { get; set; }
    public string PaymentStatus { get; set; } = null!;
    public string? ReferenceNumber { get; set; }
    public string ProcessedBy { get; set; } = null!;
    public DateTime PaidAt { get; set; }
}
