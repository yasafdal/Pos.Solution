namespace POS.Shared.Models;

public class ServiceModeDto
{
    public int ServiceModeId { get; set; }
    public string Type { get; set; } = null!;
    public string Description { get; set; } = null!;
}
