using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class BranchDto
{
    public int BranchId { get; set; }
    public int? BusinessTypeId { get; set; }
    public string? BusinessTypeName { get; set; }
    public int? ServiceModeId { get; set; }
    public string? ServiceModeName { get; set; }
    public string Name { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.StringLength(200)]
    public string? BranchTagline { get; set; }
    public string? Code { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }

    // BIR Tax Type (VAT-Registered, Non-VAT)
    public string TaxType { get; set; } = "VAT-Registered";
    public bool VatDisplay { get; set; } = true;
    public bool ShowCustomerDetailsOnReceipt { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public int OutletCount { get; set; }
    public int TerminalCount { get; set; }
    public List<OutletDto> Outlets { get; set; } = new();

    public bool TrackInventory { get; set; }
    public string? ReceiptLogoUrl { get; set; }
    public string? ReceiptLogoDataUri { get; set; }
    public DateTime? ReceiptLogoUpdatedAt { get; set; }
    public List<TerminalDto> Terminals { get; set; } = new();
}
