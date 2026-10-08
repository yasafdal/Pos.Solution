using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class BranchDto
{
    public int BranchId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Address { get; set; }
    public string? Phone { get; set; }

    // BIR Tax Type (VAT-Registered, Non-VAT)
    public string TaxType { get; set; } = "VAT-Registered";

    public bool IsActive { get; set; } = true;
    public int OutletCount { get; set; }
    public List<OutletDto> Outlets { get; set; } = new();
}
