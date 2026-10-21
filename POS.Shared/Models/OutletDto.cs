using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class OutletDto
{
    public int OutletId { get; set; }
    [Range(1, int.MaxValue)] public int BranchId { get; set; }
    [Required, StringLength(30)] public string OutletCode { get; set; } = string.Empty;
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(255)] public string? Description { get; set; }
    [StringLength(255)] public string? LocationDescription { get; set; }
    [Required, RegularExpression("^(Main|Booth|Kiosk|Counter|Mobile)$")]
    public string OutletType { get; set; } = "Main";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int TerminalCount { get; set; }
    public List<TerminalDto> Terminals { get; set; } = new();
}
