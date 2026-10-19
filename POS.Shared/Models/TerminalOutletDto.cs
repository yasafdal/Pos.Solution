namespace POS.Shared.Models;

public class TerminalOutletDto
{
    public int OutletId { get; set; }
    public int BranchId { get; set; }
    public int? BusinessTypeId { get; set; }
    public string? BusinessType { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public string? Token { get; set; }
}
