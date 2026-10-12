namespace POS.Shared.Models;

public class PaymentProcessDto
{
    public long OrderId { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public decimal Amount { get; set; }
    public decimal AmountTendered { get; set; }
    public string? ReferenceNumber { get; set; }
    public string ProcessedBy { get; set; } = null!;
}
