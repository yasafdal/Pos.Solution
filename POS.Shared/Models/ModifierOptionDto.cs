using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class ModifierOptionDto
{
    public int ModifierOptionId { get; set; }
    public int ModifierGroupId { get; set; }
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
