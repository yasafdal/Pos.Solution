using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class ModifierGroupDto
{
    public int ModifierGroupId { get; set; }
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; } = 1;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<ModifierOptionDto> Options { get; set; } = new();
}
