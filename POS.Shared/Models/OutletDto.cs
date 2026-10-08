using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class OutletDto
{
    public int OutletId { get; set; }
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? DeviceIdentifier { get; set; }

    public string? PTUNumber { get; set; }      // Permit To Use Number
    public string? MINNumber { get; set; }      // Machine Identification Number
    public string? SerialNumber { get; set; }   // Machine Serial Number
    public bool IsActive { get; set; } = true;
}
