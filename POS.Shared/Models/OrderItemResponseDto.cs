using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderItemResponseDto
{
    public long OrderItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal BasePrice { get; set; }
    public decimal ModifierTotal { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public List<OrderItemModifierResponseDto> Modifiers { get; set; } = new();
}
