namespace POS.Shared.Models;

public class TerminalConfigDto
{
    public int TerminalId { get; set; }
    public MenuDisplayMode MenuDisplayMode { get; set; } = MenuDisplayMode.TextOnly;
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
    public bool IsInitialized { get; set; } = false;
    public string? Token { get; set; }
    public string? Uuid { get; set; }
    public bool AllowSync { get; set; } = true;
    public decimal? DefaultOpeningFund { get; set; }
    public decimal? OpeningFundOverride { get; set; }
    public decimal? EffectiveOpeningFund { get; set; }
    public string? OpeningFundSource { get; set; }
    public string? ClosedBusinessDate { get; set; }
    public string? ClosingLockedByCashier { get; set; }
    public DateTime? ClosingLockedAtUtc { get; set; }
    public DateTime? ClosingUnlockAtUtc { get; set; }
}
