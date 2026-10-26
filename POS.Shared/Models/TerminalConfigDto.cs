namespace POS.Shared.Models;

public class TerminalConfigDto
{
    public int TerminalId { get; set; }
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public string TerminalCode { get; set; } = string.Empty;
    public string? DeviceIdentifier { get; set; }
    public string? IpAddress { get; set; }
    public string? PTUNumber { get; set; }
    public string? MINNumber { get; set; }
    public string? SerialNumber { get; set; }
    public int BranchId { get; set; }
    public int? BusinessTypeId { get; set; }
    public string? BusinessType { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string TerminalName { get; set; } = string.Empty;
    public string? ReceiptPrinterName { get; set; }
    public string? KitchenPrinterName { get; set; }
    public string? BarPrinterName { get; set; }
    public string? Token { get; set; }
}
