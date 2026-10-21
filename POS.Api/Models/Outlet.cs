namespace POS.Api.Models;

public class Outlet
{
    public int OutletId { get; set; }
    public int BranchId { get; set; }
    public string OutletCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? LocationDescription { get; set; }
    public string OutletType { get; set; } = "Main";
    public bool IsActive { get; set; } = true;
    public DateTime? DeactivatedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Branch Branch { get; set; } = null!;
    public ICollection<Terminal> Terminals { get; set; } = new List<Terminal>();
}
