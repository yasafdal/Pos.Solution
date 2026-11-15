using System.Collections.Generic;

namespace POS.Shared.Models;

public class OrderItemCreateDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public string? ProductShortCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal BasePrice { get; set; }
    public string? PricingMethod { get; set; }
    public string? UnitOfMeasure { get; set; }
    public decimal? TargetAmount { get; set; }
    public bool IsComplimentary { get; set; } = false;
    public int? ParentProductId { get; set; }
    public string? TaxName { get; set; } = "VATable";
    public bool IsKitchenItem { get; set; }
    public bool IsBarItem { get; set; }
    [System.ComponentModel.DataAnnotations.MaxLength(CartItem.MaxSpecialInstructionsLength)]
    public string? SpecialInstructions { get; set; }
    public List<OrderItemModifierCreateDto> Modifiers { get; set; } = new();
}
