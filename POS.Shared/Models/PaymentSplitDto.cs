namespace POS.Shared.Models;

public class PaymentSplitDto
{
    public string PaymentMethod { get; set; } = "CASH";
    public decimal Amount { get; set; }
    public decimal AmountTendered { get; set; }
    public string? ReferenceNumber { get; set; }
}
