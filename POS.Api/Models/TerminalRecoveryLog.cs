namespace POS.Api.Models;

public class TerminalRecoveryLog
{
    public long RecoveryId { get; set; }
    public int TerminalId { get; set; }
    public string EntityIdentifier { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string RecoveredBy { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime RecoveredAt { get; set; }
}
