using System.ComponentModel.DataAnnotations;
using POS.Shared.Helpers;

namespace POS.Shared.Models;

public class CategoryDto
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    [Required]
    [RegularExpression(@"\A#[0-9A-Fa-f]{6}\z", ErrorMessage = "Color must be a six-digit hex value, such as #2563EB.")]
    public string ColorHex { get; set; } = CategoryColor.Default;

    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }

    public int ProductCount { get; set; }
}
