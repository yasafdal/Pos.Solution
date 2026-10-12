using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderItemCreateDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal BasePrice { get; set; }
    public List<OrderItemModifierCreateDto> Modifiers { get; set; } = new();
}
