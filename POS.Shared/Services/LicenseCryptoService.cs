using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using POS.Shared.Models;

namespace POS.Shared.Services;

/// <summary>
/// Provides asymmetric ECDSA P-256 (SHA-256) signature verification and deterministic
/// license state evaluation shared by POS.Api and POS.Client.
/// CRITICAL SECURITY RULE: This shared class contains ONLY public verification keys.
/// Private signing keys exist exclusively inside POS.Api server configuration.
/// </summary>
public static class LicenseCryptoService
{
    // NIST P-256 Public Verification Keys (QX, QY) — safe for distribution in POS.Client
    private const string DefaultProdPublicKeyQX = "/5VnWXnDM99jUZcO5TWpDbj5f4IHesHX8dN8ljQdIxo=";
    private const string DefaultProdPublicKeyQY = "6prnBjYqFUAsu0yVvSfTHSZjZMw9zhP+mVcDjgFEqDQ=";

    private const string DefaultUatPublicKeyQX = "Zw/9f1cKIFpiE30vF7PSR9WVjsUtRv+tjA6MbslqbCw=";
    private const string DefaultUatPublicKeyQY = "mKxV1HQS/4jNuogxZGmA0XcdK/wUN569XAmp6Kl/lIA=";

    public static readonly TimeSpan ClockRollbackTolerance = TimeSpan.FromMinutes(10);
    public const int DefaultLicenseDurationYears = 5;
    public const int DefaultOfflineGracePeriodDays = 30;

    public static DateTime NormalizeUtcSecond(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local
            ? value.ToUniversalTime()
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
    }

    public static DateTime? NormalizeUtcSecond(DateTime? value) =>
        value.HasValue ? NormalizeUtcSecond(value.Value) : null;

    public static string NormalizeEnvironment(string? environmentType)
    {
        if (string.IsNullOrWhiteSpace(environmentType))
            return LicenseEnvironmentConstants.Production;
        var trimmed = environmentType.Trim();
        if (trimmed.Equals("UAT", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Development", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Staging", StringComparison.OrdinalIgnoreCase))
        {
            return LicenseEnvironmentConstants.Uat;
        }
        return LicenseEnvironmentConstants.Production;
    }

    public static string GenerateLicenseKey(int branchId, int outletId, int terminalId)
    {
        Span<byte> randomBytes = stackalloc byte[8];
        RandomNumberGenerator.Fill(randomBytes);
        var hex = Convert.ToHexString(randomBytes);
        return $"POSLIC-B{branchId:D2}O{outletId:D2}-T{terminalId:D3}-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
    }

    public static bool TryNormalizeDuration(
        int requestedValue,
        string? requestedUnit,
        int? fallbackYears,
        out int normalizedValue,
        out string normalizedUnit,
        out string? errorMessage)
    {
        if (requestedValue < 0)
        {
            normalizedValue = 0;
            normalizedUnit = LicenseDurationUnitConstants.Years;
            errorMessage = "License duration value cannot be negative.";
            return false;
        }

        int effectiveVal = requestedValue > 0 ? requestedValue : (fallbackYears ?? 0);
        string rawUnit = string.IsNullOrWhiteSpace(requestedUnit)
            ? LicenseDurationUnitConstants.Years
            : requestedUnit.Trim().ToUpperInvariant();

        normalizedUnit = rawUnit switch
        {
            "Y" or "YR" or "YRS" or "YEAR" or "YEARS" => LicenseDurationUnitConstants.Years,
            "M" or "MO" or "MOS" or "MONTH" or "MONTHS" => LicenseDurationUnitConstants.Months,
            "D" or "DAY" or "DAYS" => LicenseDurationUnitConstants.Days,
            _ => string.Empty
        };

        if (string.IsNullOrEmpty(normalizedUnit))
        {
            normalizedValue = 0;
            errorMessage = $"Unsupported license duration unit '{requestedUnit}'. Allowed units: YEARS, MONTHS, DAYS.";
            return false;
        }

        if (effectiveVal <= 0)
        {
            normalizedValue = 0;
            errorMessage = "License duration value must be a positive integer greater than 0.";
            return false;
        }

        int maxAllowed = normalizedUnit switch
        {
            LicenseDurationUnitConstants.Years => 50,
            LicenseDurationUnitConstants.Months => 600,
            LicenseDurationUnitConstants.Days => 18250,
            _ => 50
        };

        if (effectiveVal > maxAllowed)
        {
            normalizedValue = 0;
            errorMessage = $"License duration {effectiveVal} {normalizedUnit} exceeds maximum allowed limit ({maxAllowed} {normalizedUnit}).";
            return false;
        }

        normalizedValue = effectiveVal;
        errorMessage = null;
        return true;
    }

    public static DateTime CalculateExpirationUtc(DateTime startUtc, int durationValue, string? durationUnit)
    {
        var normStart = NormalizeUtcSecond(startUtc);
        if (!TryNormalizeDuration(durationValue, durationUnit, DefaultLicenseDurationYears, out int val, out string unit, out var error))
        {
            throw new ArgumentException(error ?? "Invalid license duration.", nameof(durationValue));
        }

        DateTime raw = unit switch
        {
            LicenseDurationUnitConstants.Years => normStart.AddYears(val),
            LicenseDurationUnitConstants.Months => normStart.AddMonths(val),
            LicenseDurationUnitConstants.Days => normStart.AddDays(val),
            _ => normStart.AddYears(val)
        };

        return NormalizeUtcSecond(raw);
    }

    public static string FormatDurationLabel(int durationValue, string? durationUnit)
    {
        int val = durationValue > 0 ? durationValue : DefaultLicenseDurationYears;
        string unit = string.IsNullOrWhiteSpace(durationUnit) ? LicenseDurationUnitConstants.Years : durationUnit.Trim().ToUpperInvariant();
        string singular = unit switch
        {
            LicenseDurationUnitConstants.Months => "Month",
            LicenseDurationUnitConstants.Days => "Day",
            _ => "Year"
        };
        return val == 1 ? $"1 {singular}" : $"{val} {singular}s";
    }

    public static int ApproximateDurationYears(int durationValue, string? durationUnit)
    {
        int val = Math.Max(1, durationValue);
        string unit = (durationUnit ?? LicenseDurationUnitConstants.Years).Trim().ToUpperInvariant();
        return unit switch
        {
            LicenseDurationUnitConstants.Months => Math.Max(1, (int)Math.Round(val / 12.0)),
            LicenseDurationUnitConstants.Days => Math.Max(1, (int)Math.Round(val / 365.0)),
            _ => val
        };
    }

    /// <summary>
    /// Builds the canonical deterministic string that is cryptographically signed and verified.
    /// Any modification to any field in PostgreSQL or SQLite changes this string and invalidates the signature.
    /// </summary>
    public static string BuildCanonicalString(CanonicalLicensePayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var issuedStr = NormalizeUtcSecond(payload.IssuedAtUtc).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var activatedStr = payload.ActivatedAtUtc.HasValue
            ? NormalizeUtcSecond(payload.ActivatedAtUtc.Value).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : "NULL";
        var expiresStr = payload.ExpiresAtUtc.HasValue
            ? NormalizeUtcSecond(payload.ExpiresAtUtc.Value).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            : "NULL";
        var env = NormalizeEnvironment(payload.EnvironmentType);
        var status = (payload.LicenseStatus ?? LicenseStatusConstants.PendingActivation).Trim().ToUpperInvariant();
        var deviceId = (payload.DeviceId ?? string.Empty).Trim().ToUpperInvariant();
        int durVal = payload.DurationValue > 0 ? payload.DurationValue : DefaultLicenseDurationYears;
        string durUnit = string.IsNullOrWhiteSpace(payload.DurationUnit) ? LicenseDurationUnitConstants.Years : payload.DurationUnit.Trim().ToUpperInvariant();
        int origVal = payload.OriginalDurationValue > 0 ? payload.OriginalDurationValue : durVal;
        string origUnit = string.IsNullOrWhiteSpace(payload.OriginalDurationUnit) ? durUnit : payload.OriginalDurationUnit.Trim().ToUpperInvariant();

        return string.Create(CultureInfo.InvariantCulture,
            $"POS-LIC-V2|{payload.LicenseId.Trim()}|{payload.LicenseKey.Trim()}|{payload.TerminalId}|{deviceId}|{payload.BranchId}|{payload.OutletId}|{issuedStr}|{activatedStr}|{expiresStr}|{status}|{payload.LicenseVersion}|{payload.OfflineGracePeriodDays}|{env}|{durVal}|{durUnit}|{origVal}|{origUnit}");
    }

    /// <summary>
    /// Signs the canonical payload using ECDSA P-256 SHA-256. Called ONLY by POS.Api with its private key.
    /// </summary>
    public static string SignPayload(CanonicalLicensePayload payload, ECParameters privateKeyParameters)
    {
        var canonical = BuildCanonicalString(payload);
        var data = Encoding.UTF8.GetBytes(canonical);
        using var ecdsa = ECDsa.Create(privateKeyParameters);
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(sig);
    }

    public static ECParameters GetPublicKeyParameters(string? environmentType)
    {
        var env = NormalizeEnvironment(environmentType);
        bool isUat = string.Equals(env, LicenseEnvironmentConstants.Uat, StringComparison.OrdinalIgnoreCase);
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Convert.FromBase64String(isUat ? DefaultUatPublicKeyQX : DefaultProdPublicKeyQX),
                Y = Convert.FromBase64String(isUat ? DefaultUatPublicKeyQY : DefaultProdPublicKeyQY)
            }
        };
    }

    /// <summary>
    /// Verifies the ECDSA P-256 digital signature of a license payload using the public verification key.
    /// </summary>
    public static bool VerifySignature(CanonicalLicensePayload? payload, string? signatureBase64, string? requiredEnvironment = null)
    {
        if (payload == null || string.IsNullOrWhiteSpace(signatureBase64))
            return false;

        var payloadEnv = NormalizeEnvironment(payload.EnvironmentType);
        if (!string.IsNullOrWhiteSpace(requiredEnvironment))
        {
            var expectedEnv = NormalizeEnvironment(requiredEnvironment);
            if (!string.Equals(payloadEnv, expectedEnv, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        byte[] sigBytes;
        try
        {
            sigBytes = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch
        {
            return false;
        }

        try
        {
            var canonical = BuildCanonicalString(payload);
            var data = Encoding.UTF8.GetBytes(canonical);
            var pubParams = GetPublicKeyParameters(payloadEnv);
            using var ecdsa = ECDsa.Create(pubParams);
            return ecdsa.VerifyData(data, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }

    public static int? GetWarningThresholdDays(int remainingDays)
    {
        if (remainingDays <= 0) return null;
        if (remainingDays <= 7) return 7;
        if (remainingDays <= 30) return 30;
        if (remainingDays <= 60) return 60;
        if (remainingDays <= 90) return 90;
        return null;
    }

    /// <summary>
    /// Evaluates the complete cryptographic, device-binding, clock-tamper, offline-grace, and expiration state of a terminal license.
    /// </summary>
    public static LicenseEvaluationResultDto EvaluateLicense(
        CanonicalLicensePayload? payload,
        string? signatureBase64,
        int expectedTerminalId,
        int expectedBranchId,
        int expectedOutletId,
        string? currentDeviceId,
        string? expectedEnvironment,
        DateTime deviceUtcNow,
        long currentTickCount64,
        DateTime? lastValidatedServerUtc,
        long? lastValidatedTickCount64,
        DateTime? highWaterMarkUtc,
        bool isServerSideEvaluation = false)
    {
        var normDeviceUtc = NormalizeUtcSecond(deviceUtcNow);

        if (payload == null)
        {
            return new LicenseEvaluationResultDto
            {
                IsValidForNewSales = false,
                IsValidForNewShift = false,
                EffectiveStatus = LicenseStatusConstants.ValidationRequired,
                StoredStatus = LicenseStatusConstants.PendingActivation,
                BlockReasonCode = "LICENSE_MISSING",
                BlockMessage = "No terminal license is installed on this device. Connect to the server to activate or validate the terminal license.",
                EvaluatedAtUtc = normDeviceUtc,
                TerminalId = expectedTerminalId,
                BranchId = expectedBranchId,
                OutletId = expectedOutletId
            };
        }

        int durVal = payload.DurationValue > 0 ? payload.DurationValue : DefaultLicenseDurationYears;
        string durUnit = string.IsNullOrWhiteSpace(payload.DurationUnit) ? LicenseDurationUnitConstants.Years : payload.DurationUnit.Trim().ToUpperInvariant();
        int origDurVal = payload.OriginalDurationValue > 0 ? payload.OriginalDurationValue : durVal;
        string origDurUnit = string.IsNullOrWhiteSpace(payload.OriginalDurationUnit) ? durUnit : payload.OriginalDurationUnit.Trim().ToUpperInvariant();

        var result = new LicenseEvaluationResultDto
        {
            LicenseId = payload.LicenseId,
            LicenseKey = payload.LicenseKey,
            TerminalId = payload.TerminalId,
            BranchId = payload.BranchId,
            OutletId = payload.OutletId,
            DeviceId = payload.DeviceId,
            StoredStatus = payload.LicenseStatus,
            LicenseVersion = payload.LicenseVersion,
            OfflineGracePeriodDays = payload.OfflineGracePeriodDays > 0 ? payload.OfflineGracePeriodDays : DefaultOfflineGracePeriodDays,
            EnvironmentType = NormalizeEnvironment(payload.EnvironmentType),
            ActivatedAtUtc = NormalizeUtcSecond(payload.ActivatedAtUtc),
            ExpiresAtUtc = NormalizeUtcSecond(payload.ExpiresAtUtc),
            LastValidatedAtUtc = NormalizeUtcSecond(lastValidatedServerUtc),
            EvaluatedAtUtc = normDeviceUtc,
            DurationValue = durVal,
            DurationUnit = durUnit,
            OriginalDurationValue = origDurVal,
            OriginalDurationUnit = origDurUnit
        };

        // 1. Environment Isolation Check (Phase 15)
        var targetEnv = string.IsNullOrWhiteSpace(expectedEnvironment)
            ? result.EnvironmentType
            : NormalizeEnvironment(expectedEnvironment);
        result.IsEnvironmentValid = string.Equals(result.EnvironmentType, targetEnv, StringComparison.OrdinalIgnoreCase);
        if (!result.IsEnvironmentValid)
        {
            result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
            result.BlockReasonCode = "ENVIRONMENT_MISMATCH";
            result.BlockMessage = $"License environment '{result.EnvironmentType}' is not valid for '{targetEnv}' operation.";
            return result;
        }

        // 2. Cryptographic Signature Verification (Phase 4)
        result.IsSignatureValid = VerifySignature(payload, signatureBase64, targetEnv);
        if (!result.IsSignatureValid)
        {
            result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
            result.BlockReasonCode = "SIGNATURE_INVALID";
            result.BlockMessage = "License integrity check failed (digital signature mismatch). Local or database license data may have been modified.";
            return result;
        }

        // 3. Explicit Revocation / Suspension Check (Phase 9)
        var storedUpper = (payload.LicenseStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (storedUpper == LicenseStatusConstants.Revoked)
        {
            result.EffectiveStatus = LicenseStatusConstants.Revoked;
            result.BlockReasonCode = "LICENSE_REVOKED";
            result.BlockMessage = "This terminal license has been revoked by an Administrator. New sales and shifts are disabled.";
            return result;
        }

        if (storedUpper == LicenseStatusConstants.Suspended)
        {
            result.EffectiveStatus = LicenseStatusConstants.Suspended;
            result.BlockReasonCode = "LICENSE_SUSPENDED";
            result.BlockMessage = "This terminal license is currently suspended by an Administrator. New sales and shifts are disabled.";
            return result;
        }

        if (storedUpper == LicenseStatusConstants.PendingActivation || !result.ActivatedAtUtc.HasValue || !result.ExpiresAtUtc.HasValue)
        {
            result.EffectiveStatus = LicenseStatusConstants.PendingActivation;
            result.BlockReasonCode = "PENDING_ACTIVATION";
            result.BlockMessage = "Terminal license is pending activation. Activate the license online before processing sales.";
            return result;
        }

        // 4. Terminal / Branch / Outlet / Hardware Device Binding Check (Phase 5)
        bool hierarchyMatches =
            (expectedTerminalId <= 0 || payload.TerminalId == expectedTerminalId) &&
            (expectedBranchId <= 0 || payload.BranchId == expectedBranchId) &&
            (expectedOutletId <= 0 || payload.OutletId == expectedOutletId);

        bool deviceMatches = !string.IsNullOrWhiteSpace(payload.DeviceId) &&
                             !string.IsNullOrWhiteSpace(currentDeviceId) &&
                             string.Equals(payload.DeviceId.Trim(), currentDeviceId.Trim(), StringComparison.OrdinalIgnoreCase);

        result.IsDeviceBoundValid = hierarchyMatches && deviceMatches;
        if (!result.IsDeviceBoundValid)
        {
            result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
            result.BlockReasonCode = !hierarchyMatches ? "TERMINAL_MISMATCH" : "DEVICE_MISMATCH";
            result.BlockMessage = !hierarchyMatches
                ? "License does not belong to this Branch / Outlet / Terminal configuration."
                : "License is bound to a different physical device. Copying the local database to another device is prohibited. Contact an Administrator to authorize device replacement.";
            return result;
        }

        // 5. Clock Rollback & Monotonic Time Evaluation (Phase 8)
        DateTime referenceFloorUtc = result.ActivatedAtUtc.Value;
        DateTime? monotonicServerUtc = null;

        if (result.LastValidatedAtUtc.HasValue)
        {
            if (result.LastValidatedAtUtc.Value > referenceFloorUtc)
                referenceFloorUtc = result.LastValidatedAtUtc.Value;

            if (lastValidatedTickCount64.HasValue && currentTickCount64 >= lastValidatedTickCount64.Value)
            {
                var elapsedMs = currentTickCount64 - lastValidatedTickCount64.Value;
                monotonicServerUtc = result.LastValidatedAtUtc.Value.AddMilliseconds(elapsedMs);
                if (monotonicServerUtc.Value > referenceFloorUtc)
                    referenceFloorUtc = monotonicServerUtc.Value;
            }
        }

        if (highWaterMarkUtc.HasValue)
        {
            var normHwm = NormalizeUtcSecond(highWaterMarkUtc.Value);
            if (normHwm > referenceFloorUtc)
                referenceFloorUtc = normHwm;
        }

        if (!isServerSideEvaluation && normDeviceUtc + ClockRollbackTolerance < referenceFloorUtc)
        {
            result.IsClockRollbackDetected = true;
            result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
            result.BlockReasonCode = "CLOCK_ROLLBACK_DETECTED";
            result.BlockMessage = $"System clock rollback detected (device UTC {normDeviceUtc:yyyy-MM-dd HH:mm} is earlier than trusted reference {referenceFloorUtc:yyyy-MM-dd HH:mm}). Restore accurate system time and revalidate online.";
            return result;
        }

        // Effective UTC for expiration and offline grace is the maximum of device UTC and monotonic/high-water reference
        DateTime effectiveUtcNow = normDeviceUtc > referenceFloorUtc ? normDeviceUtc : referenceFloorUtc;
        result.EvaluatedAtUtc = effectiveUtcNow;

        // 6. Configurable Expiration Check
        var expiresAt = result.ExpiresAtUtc.Value;
        if (effectiveUtcNow >= expiresAt || storedUpper == LicenseStatusConstants.Expired)
        {
            result.RemainingDays = 0;
            result.EffectiveStatus = LicenseStatusConstants.Expired;
            result.BlockReasonCode = "LICENSE_EXPIRED";
            result.BlockMessage = $"Terminal license ({FormatDurationLabel(result.DurationValue, result.DurationUnit)}) expired on {expiresAt:yyyy-MM-dd HH:mm} UTC. Please renew the license in POS.Web to resume sales.";
            return result;
        }

        var remainingSpan = expiresAt - effectiveUtcNow;
        int remainingDays = Math.Max(1, (int)Math.Ceiling(remainingSpan.TotalDays));
        result.RemainingDays = remainingDays;

        // 7. Offline Grace Period Check (Phase 7)
        if (!isServerSideEvaluation)
        {
            if (!result.LastValidatedAtUtc.HasValue)
            {
                result.IsOfflineGraceExpired = true;
                result.RemainingOfflineGraceDays = 0;
                result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
                result.BlockReasonCode = "OFFLINE_VALIDATION_MISSING";
                result.BlockMessage = "Online license validation timestamp is missing. Connect to the server to validate the terminal license.";
                return result;
            }

            var offlineElapsed = effectiveUtcNow - result.LastValidatedAtUtc.Value;
            var graceWindow = TimeSpan.FromDays(result.OfflineGracePeriodDays);
            if (offlineElapsed > graceWindow)
            {
                result.IsOfflineGraceExpired = true;
                result.RemainingOfflineGraceDays = 0;
                result.EffectiveStatus = LicenseStatusConstants.ValidationRequired;
                result.BlockReasonCode = "OFFLINE_GRACE_EXPIRED";
                result.BlockMessage = $"Offline license validation grace period ({result.OfflineGracePeriodDays} days) has elapsed since {result.LastValidatedAtUtc.Value:yyyy-MM-dd}. Connect to POS.API to revalidate the license.";
                return result;
            }

            result.RemainingOfflineGraceDays = Math.Max(0, (int)Math.Ceiling((graceWindow - offlineElapsed).TotalDays));
        }
        else
        {
            result.RemainingOfflineGraceDays = result.OfflineGracePeriodDays;
        }

        // 8. Expiration Warning Thresholds (90, 60, 30, 7 days)
        var threshold = GetWarningThresholdDays(remainingDays);
        if (threshold.HasValue)
        {
            result.EffectiveStatus = LicenseStatusConstants.ExpiringSoon;
            result.WarningThresholdDays = threshold.Value;
            result.WarningMessage = $"License Expiring Soon ({threshold.Value}-Day Notice): {remainingDays} day(s) remaining until {expiresAt:yyyy-MM-dd}. Please schedule license renewal.";
        }
        else
        {
            result.EffectiveStatus = LicenseStatusConstants.Active;
        }

        result.IsValidForNewSales = true;
        result.IsValidForNewShift = true;
        return result;
    }
}
