using System.Collections.Generic;

namespace POS.Shared.Models;

public class ProductModifierGroupDto
{
    public int ProductModifierGroupId { get; set; }
    public int ProductId { get; set; }
    public int ModifierGroupId { get; set; }
    public string? ModifierGroupName { get; set; }
    public bool IsRequired { get; set; }
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
    public int DisplayOrder { get; set; }
    public List<ModifierOptionDto> Options { get; set; } = new();
}
