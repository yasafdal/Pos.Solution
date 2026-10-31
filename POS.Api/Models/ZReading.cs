namespace POS.Api.Models;

public sealed class ZReadingTerminalState
{
    public int TerminalId { get; set; }
    public DateOnly FirstBusinessDate { get; set; }
    public decimal OpeningAccumulatedSales { get; set; }
    public decimal AccumulatedGrossSales { get; set; }
    public long MutationVersion { get; set; }
    public long LastZNumber { get; set; }
    public long? ResetCounter { get; set; }
    public DateOnly? LastBusinessDate { get; set; }
    public string ConfiguredBy { get; set; } = "";
    public DateTime ConfiguredAt { get; set; } = DateTime.UtcNow;
}

public sealed class ZReading
{
    public Guid Id { get; set; }
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTime PeriodStartUtc { get; set; }
    public DateTime PeriodEndUtc { get; set; }
    public DateTime GeneratedAt { get; set; }
    public string GeneratedBy { get; set; } = "";
    public string ClientOperationId { get; set; } = "";
    public string DeviceIdentifier { get; set; } = "";
    public long ZNumber { get; set; }
    public string SnapshotJson { get; set; } = "";
    public string SnapshotSha256 { get; set; } = "";
}

// Both start and completion are append-only. A crashed STARTED attempt remains visibly unresolved.
public sealed class ZReadingPrintAttempt
{
    public Guid Id { get; set; }
    public Guid ZReadingId { get; set; }
    public string ClientOperationId { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string? PrinterName { get; set; }
    public string? Reason { get; set; }
}

public sealed class ZReadingPrintResult
{
    public Guid AttemptId { get; set; }
    public string Status { get; set; } = "UNKNOWN";
    public string? ErrorMessage { get; set; }
    public DateTime CompletedAt { get; set; }
    public string CompletedBy { get; set; } = "";
}
