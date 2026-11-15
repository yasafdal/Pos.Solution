using System;

namespace POS.Api.Models;

public class TerminalLicense
{
    public string LicenseId { get; set; } = Guid.NewGuid().ToString("N");
    public string LicenseKey { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string LicenseStatus { get; set; } = "PENDING_ACTIVATION";
    public int DurationYears { get; set; } = 5;
    public int DurationValue { get; set; } = 5;
    public string DurationUnit { get; set; } = "YEARS";
    public int OriginalDurationValue { get; set; } = 5;
    public string OriginalDurationUnit { get; set; } = "YEARS";
    public int RenewalCount { get; set; }
    public DateTime? LastRenewedAt { get; set; }
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ActivatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastValidatedAt { get; set; }
    public int LicenseVersion { get; set; } = 1;
    public DateTime? RevokedAt { get; set; }
    public string? RevocationReason { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public string? SuspensionReason { get; set; }
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string EnvironmentType { get; set; } = "Production";
    public string Signature { get; set; } = string.Empty;
    public string? IssuedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public virtual Terminal Terminal { get; set; } = null!;
    public virtual Branch Branch { get; set; } = null!;
    public virtual Outlet Outlet { get; set; } = null!;
}

public class LicenseRenewalHistory
{
    public long RenewalId { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public DateTime? PreviousExpiresAt { get; set; }
    public int RenewalDuration { get; set; } = 5;
    public string RenewalUnit { get; set; } = "YEARS";
    public DateTime NewExpiresAt { get; set; }
    public int PreviousLicenseVersion { get; set; }
    public int NewLicenseVersion { get; set; }
    public string RenewedBy { get; set; } = string.Empty;
    public string? RenewalReason { get; set; }
    public string? RenewalNotes { get; set; }
    public string RenewalStatus { get; set; } = "COMPLETED";
    public string? IdempotencyKey { get; set; }
    public DateTime RenewedAt { get; set; } = DateTime.UtcNow;
}

public class LicenseDeviceHistory
{
    public long DeviceHistoryId { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string? OldDeviceId { get; set; }
    public string? NewDeviceId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}

public class LicenseAuditLog
{
    public long AuditLogId { get; set; }
    public string? LicenseId { get; set; }
    public int? TerminalId { get; set; }
    public int? BranchId { get; set; }
    public int? OutletId { get; set; }
    public string? DeviceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EventStatus { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Actor { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? DeviceClockUtc { get; set; }
    public DateTime ServerClockUtc { get; set; } = DateTime.UtcNow;
    public int? ClockSkewSeconds { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
}
