using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class TerminalDto
{
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    [System.ComponentModel.DataAnnotations.Range(1, int.MaxValue)] public int OutletId { get; set; }
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(30)]
    public string TerminalCode { get; set; } = string.Empty;
    public string OutletName { get; set; } = string.Empty;
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string BranchName { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.StringLength(100)]
    public string Name { get; set; } = string.Empty;
    public string? DeviceIdentifier { get; set; }

    public string? PTUNumber { get; set; }      // Permit To Use Number
    public string? MINNumber { get; set; }      // Machine Identification Number
    public string? SerialNumber { get; set; }   // Machine Serial Number
    public bool IsActive { get; set; } = true;
}
