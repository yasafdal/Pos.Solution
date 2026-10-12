using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderCreateDto
{
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = null!;
    public long? ShiftId { get; set; }
    public string OrderType { get; set; } = "DINING";
    public decimal DiscountAmount { get; set; }
    public List<OrderItemCreateDto> Items { get; set; } = new();
}
