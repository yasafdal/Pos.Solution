using System;

namespace POS.Shared.Models;

public class ReceiptBrandingDto
{
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string? ReceiptLogoUrl { get; set; }
    public string? ReceiptLogoDataUri { get; set; }
    public DateTime? ReceiptLogoUpdatedAt { get; set; }
    public bool ShowCustomerDetailsOnReceipt { get; set; } = true;
    public bool HasLogo => !string.IsNullOrWhiteSpace(ReceiptLogoUrl);
}

public class UpdateReceiptBrandingSettingsDto
{
    public bool ShowCustomerDetailsOnReceipt { get; set; } = true;
    public bool ApplyToAllBranches { get; set; }
}

public class ReceiptBrandingResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ReceiptBrandingDto? Branding { get; set; }
}
