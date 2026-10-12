using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class OrderItemModifier
{
    public long OrderItemModifierId { get; set; }

    public long OrderItemId { get; set; }

    public int ModifierOptionId { get; set; }

    [Required]
    [MaxLength(100)]
    public string ModifierName { get; set; } = null!;

    public decimal AdditionalPrice { get; set; }

    public virtual OrderItem OrderItem { get; set; } = null!;
}
