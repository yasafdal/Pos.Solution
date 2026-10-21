using System;
using System.ComponentModel.DataAnnotations;

namespace POS.Api.Models;

public partial class CashierSession
{
    [Key]
    public long CashierSessionId { get; set; }
    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    [MaxLength(100)]
    public string? DeviceIdentifier { get; set; }
    public DateTime LoginAt { get; set; } = DateTime.UtcNow;
    public DateTime? LogoutAt { get; set; }
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "ACTIVE";
    [MaxLength(100)]
    public string? LogoutReason { get; set; }
    public long? PreviousSessionId { get; set; }
}

public partial class CashierSwitchLog
{
    [Key]
    public long SwitchLogId { get; set; }
    public int TerminalId { get; set; }
    [MaxLength(100)]
    public string? DeviceIdentifier { get; set; }
    [Required]
    [MaxLength(450)]
    public string PreviousCashierId { get; set; } = null!;
    [Required]
    [MaxLength(450)]
    public string NewCashierId { get; set; } = null!;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    [Required]
    [MaxLength(30)]
    public string Status { get; set; } = "SUCCESS";
    [MaxLength(255)]
    public string? Reason { get; set; }
}
