using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class Terminal
{
    public int TerminalId { get; set; }

    // Retained during migration; composite ownership constraint prevents branch/outlet mismatch.
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public virtual Outlet Outlet { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? DeviceIdentifier { get; set; }

    public bool IsActive { get; set; }

    public string? Ptunumber { get; set; }

    public string? Minnumber { get; set; }

    public string? SerialNumber { get; set; }
    public DateTime? DeactivatedAt { get; set; }
    public virtual Branch Branch { get; set; } = null!;
}
