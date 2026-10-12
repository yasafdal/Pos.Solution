namespace POS.Shared.Models;

public class OrderItemModifierResponseDto
{
    public long OrderItemModifierId { get; set; }
    public int ModifierOptionId { get; set; }
    public string ModifierName { get; set; } = null!;
    public decimal AdditionalPrice { get; set; }
}
