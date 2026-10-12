namespace POS.Api.Models;

public partial class ProductModifierGroup
{
    public int ProductModifierGroupId { get; set; }
    public int ProductId { get; set; }
    public int ModifierGroupId { get; set; }
    public int DisplayOrder { get; set; }

    public virtual Product Product { get; set; } = null!;
    public virtual ModifierGroup ModifierGroup { get; set; } = null!;
}
