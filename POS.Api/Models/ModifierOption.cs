namespace POS.Api.Models;

public partial class ModifierOption
{
    public int ModifierOptionId { get; set; }
    public int ModifierGroupId { get; set; }
    public string Name { get; set; } = null!;
    public decimal Price { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }

    public virtual ModifierGroup ModifierGroup { get; set; } = null!;
}
