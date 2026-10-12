namespace POS.Shared.Models;

public class OrderItemModifierCreateDto
{
    public int ModifierOptionId { get; set; }
    public string ModifierName { get; set; } = null!;
    public decimal AdditionalPrice { get; set; }
}
