using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class TerminalDto
{
    public int TerminalId { get; set; }
    [System.ComponentModel.DataAnnotations.EnumDataType(typeof(MenuDisplayMode))]
    public MenuDisplayMode MenuDisplayMode { get; set; } = MenuDisplayMode.TextOnly;
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
    public string? Uuid { get; set; }
    public string? ActivationCode { get; set; }
    public bool IsActivationCodeUsed { get; set; } = false;
    public bool AllowSync { get; set; } = true;
    [System.ComponentModel.DataAnnotations.Range(typeof(decimal), "0", "9999999999999999.99")]
    public decimal? OpeningFundOverride { get; set; }
    public decimal? OutletDefaultOpeningFund { get; set; }
    public decimal? EffectiveOpeningFund => OpeningFundOverride ?? OutletDefaultOpeningFund;
    public string OpeningFundSource => OpeningFundOverride.HasValue
        ? "TERMINAL_OVERRIDE"
        : (OutletDefaultOpeningFund.HasValue ? "OUTLET_DEFAULT" : "UNCONFIGURED");
    public bool IsOpeningFundConfigured => EffectiveOpeningFund.HasValue;
    public string? CashFundUpdatedBy { get; set; }
    public DateTime? CashFundUpdatedAt { get; set; }
    public string? ClosedBusinessDate { get; set; }
    public string? ClosingLockedByCashier { get; set; }
    public DateTime? ClosingLockedAtUtc { get; set; }
    public DateTime? ClosingUnlockAtUtc { get; set; }
}
