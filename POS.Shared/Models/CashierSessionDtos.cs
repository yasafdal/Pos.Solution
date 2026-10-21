using System;

namespace POS.Shared.Models;

public class CashierSessionActivateDto
{
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public string? DeviceIdentifier { get; set; }
}

public class CashierSessionEndDto
{
    public long CashierSessionId { get; set; }
    public string? Reason { get; set; }
}

public class CashierSessionDto
{
    public long CashierSessionId { get; set; }
    public string CashierId { get; set; } = null!;
    public int BranchId { get; set; }
    public int TerminalId { get; set; }
    public DateTime LoginAt { get; set; }
    public string Status { get; set; } = null!;
}
