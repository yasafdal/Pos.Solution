using System.Collections.Generic;

namespace POS.Shared.Models;

public class PaymentProcessDto
{
    public long? ShiftId { get; set; }
    public long OrderId { get; set; }
    public string PaymentMethod { get; set; } = "CASH";
    public decimal Amount { get; set; }
    public decimal AmountTendered { get; set; }
    public string? ReferenceNumber { get; set; }
    public string ProcessedBy { get; set; } = null!;

    public List<PaymentSplitDto>? Splits { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerTin { get; set; }
    public string? CustomerBusinessStyle { get; set; }
}
