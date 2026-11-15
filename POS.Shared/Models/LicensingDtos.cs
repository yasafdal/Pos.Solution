using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public static class LicenseStatusConstants
{
    public const string PendingActivation = "PENDING_ACTIVATION";
    public const string Active = "ACTIVE";
    public const string ExpiringSoon = "EXPIRING_SOON";
    public const string Expired = "EXPIRED";
    public const string Revoked = "REVOKED";
    public const string Suspended = "SUSPENDED";
    public const string ValidationRequired = "VALIDATION_REQUIRED";
}

public static class LicenseEnvironmentConstants
{
    public const string Production = "Production";
    public const string Uat = "UAT";
}

public static class LicenseDurationUnitConstants
{
    public const string Years = "YEARS";
    public const string Months = "MONTHS";
    public const string Days = "DAYS";
}

public static class LicenseRenewalStatusConstants
{
    public const string Completed = "COMPLETED";
    public const string Adjustment = "ADJUSTMENT";
    public const string Superseded = "SUPERSEDED";
}

/// <summary>
/// Canonical license payload covered by asymmetric ECDSA P-256 digital signature.
/// Modifying any of these fields in PostgreSQL or SQLite invalidates the signature.
/// </summary>
public class CanonicalLicensePayload
{
    public string LicenseId { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public string LicenseStatus { get; set; } = LicenseStatusConstants.PendingActivation;
    public int LicenseVersion { get; set; } = 1;
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string EnvironmentType { get; set; } = LicenseEnvironmentConstants.Production;
    public int DurationValue { get; set; } = 5;
    public string DurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public int OriginalDurationValue { get; set; } = 5;
    public string OriginalDurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
}

public class SignedLicensePackageDto
{
    public CanonicalLicensePayload Payload { get; set; } = new();
    public string Signature { get; set; } = string.Empty;
    public DateTime ServerUtcNow { get; set; } = DateTime.UtcNow;
    public string? TerminalCode { get; set; }
    public string? TerminalName { get; set; }
    public string? BranchName { get; set; }
    public string? OutletName { get; set; }
    public string? RevocationReason { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public int RenewalCount { get; set; }
    public DateTime? LastRenewedAtUtc { get; set; }
}

public class LicenseEvaluationResultDto
{
    public bool IsValidForNewSales { get; set; }
    public bool IsValidForNewShift { get; set; }
    public string EffectiveStatus { get; set; } = LicenseStatusConstants.ValidationRequired;
    public string StoredStatus { get; set; } = LicenseStatusConstants.PendingActivation;
    public bool IsSignatureValid { get; set; }
    public bool IsDeviceBoundValid { get; set; }
    public bool IsEnvironmentValid { get; set; }
    public bool IsClockRollbackDetected { get; set; }
    public bool IsOfflineGraceExpired { get; set; }
    public int? RemainingDays { get; set; }
    public int? WarningThresholdDays { get; set; }
    public int? RemainingOfflineGraceDays { get; set; }
    public string? WarningMessage { get; set; }
    public string? BlockReasonCode { get; set; }
    public string? BlockMessage { get; set; }
    public DateTime EvaluatedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string? LicenseId { get; set; }
    public string? LicenseKey { get; set; }
    public int TerminalId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string? DeviceId { get; set; }
    public int LicenseVersion { get; set; }
    public string EnvironmentType { get; set; } = LicenseEnvironmentConstants.Production;
    public int DurationValue { get; set; } = 5;
    public string DurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public int OriginalDurationValue { get; set; } = 5;
    public string OriginalDurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public int RenewalCount { get; set; }
    public DateTime? LastRenewedAtUtc { get; set; }
}

public class LicenseActivateRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    [Required]
    public string DeviceUuid { get; set; } = string.Empty;
    [Required]
    public string DeviceId { get; set; } = string.Empty;
    public string? LicenseKey { get; set; }
    public DateTime? DeviceClockUtc { get; set; }
}

public class LicenseValidateRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    [Required]
    public string DeviceUuid { get; set; } = string.Empty;
    [Required]
    public string DeviceId { get; set; } = string.Empty;
    public string? LicenseId { get; set; }
    public int? ClientLicenseVersion { get; set; }
    public DateTime? DeviceClockUtc { get; set; }
}

public class LicenseOperationResponseDto
{
    public bool Success { get; set; }
    public bool IsIdempotentReplay { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public SignedLicensePackageDto? LicensePackage { get; set; }
    public LicenseEvaluationResultDto? Evaluation { get; set; }
    public DateTime ServerUtcNow { get; set; } = DateTime.UtcNow;
}

public class TerminalLicenseAdminDto
{
    public string LicenseId { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public int BranchId { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public int OutletId { get; set; }
    public string OutletName { get; set; } = string.Empty;
    public string? DeviceId { get; set; }
    public string? TerminalUuid { get; set; }
    public string LicenseStatus { get; set; } = LicenseStatusConstants.PendingActivation;
    public string EffectiveStatus { get; set; } = LicenseStatusConstants.PendingActivation;
    public bool IsSignatureValid { get; set; } = true;
    public int DurationYears { get; set; } = 5;
    public int DurationValue { get; set; } = 5;
    public string DurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public int OriginalDurationValue { get; set; } = 5;
    public string OriginalDurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public int RenewalCount { get; set; }
    public DateTime? LastRenewedAtUtc { get; set; }
    public DateTime IssuedAtUtc { get; set; }
    public DateTime? ActivatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? LastValidatedAtUtc { get; set; }
    public int? RemainingDays { get; set; }
    public int? WarningThresholdDays { get; set; }
    public int LicenseVersion { get; set; } = 1;
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string EnvironmentType { get; set; } = LicenseEnvironmentConstants.Production;
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevocationReason { get; set; }
    public DateTime? SuspendedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
    public string? IssuedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public class IssueTerminalLicenseRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    public int DurationYears { get; set; } = 5;
    public int DurationValue { get; set; }
    public string DurationUnit { get; set; } = LicenseDurationUnitConstants.Years;
    [Range(1, 365)]
    public int OfflineGracePeriodDays { get; set; } = 30;
    public string EnvironmentType { get; set; } = LicenseEnvironmentConstants.Production;
    public bool ActivateImmediatelyIfBound { get; set; } = true;
    public DateTime? ActivationEffectiveAtUtc { get; set; }
    public string? Notes { get; set; }
}

public class RenewTerminalLicenseRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    public int AdditionalYears { get; set; } = 5;
    public int RenewalDuration { get; set; }
    public string RenewalUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public DateTime? EffectiveAtUtc { get; set; }
    public DateTime? ExplicitNewExpiresAtUtc { get; set; }
    [StringLength(100)]
    public string? IdempotencyKey { get; set; }
    public bool IsAdjustment { get; set; }
    [StringLength(250)]
    public string? RenewalReason { get; set; }
    [StringLength(250)]
    public string RenewalNotes { get; set; } = "License Renewal";
}

public class ChangeLicenseStatusRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    [Required]
    [StringLength(250, MinimumLength = 3)]
    public string Reason { get; set; } = string.Empty;
}

public class ReplaceLicenseDeviceRequestDto
{
    [Required]
    public int TerminalId { get; set; }
    public string? NewDeviceId { get; set; }
    public string? NewDeviceUuid { get; set; }
    [Required]
    [StringLength(250, MinimumLength = 3)]
    public string Reason { get; set; } = string.Empty;
}

public class LicenseRenewalHistoryDto
{
    public long RenewalId { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public DateTime? PreviousExpiresAtUtc { get; set; }
    public int RenewalDuration { get; set; }
    public string RenewalUnit { get; set; } = LicenseDurationUnitConstants.Years;
    public DateTime NewExpiresAtUtc { get; set; }
    public int PreviousLicenseVersion { get; set; }
    public int NewLicenseVersion { get; set; }
    public string RenewedBy { get; set; } = string.Empty;
    public string? RenewalReason { get; set; }
    public string? RenewalNotes { get; set; }
    public string RenewalStatus { get; set; } = LicenseRenewalStatusConstants.Completed;
    public string? IdempotencyKey { get; set; }
    public DateTime RenewedAtUtc { get; set; }
}

public class LicenseDeviceHistoryDto
{
    public long DeviceHistoryId { get; set; }
    public string LicenseId { get; set; } = string.Empty;
    public int TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public string? OldDeviceId { get; set; }
    public string? NewDeviceId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string AuthorizedBy { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public class LicenseAuditLogDto
{
    public long AuditLogId { get; set; }
    public string? LicenseId { get; set; }
    public int? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public int? BranchId { get; set; }
    public int? OutletId { get; set; }
    public string? DeviceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string EventStatus { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Actor { get; set; }
    public string? IpAddress { get; set; }
    public DateTime? DeviceClockUtc { get; set; }
    public DateTime ServerClockUtc { get; set; }
    public int? ClockSkewSeconds { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}
