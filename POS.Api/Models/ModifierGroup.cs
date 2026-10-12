using System.Collections.Generic;

namespace POS.Api.Models;

public partial class ModifierGroup
{
    public int ModifierGroupId { get; set; }
    public string Name { get; set; } = null!;
    public bool IsRequired { get; set; }
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; }

    public virtual ICollection<ModifierOption> ModifierOptions { get; set; } = new List<ModifierOption>();
    public virtual ICollection<ProductModifierGroup> ProductModifierGroups { get; set; } = new List<ProductModifierGroup>();
}
