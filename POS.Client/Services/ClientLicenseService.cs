using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public interface IClientLicenseService
{
    event Action? OnLicenseStateChanged;
    LicenseEvaluationResultDto? LastEvaluation { get; }
    Task<LicenseEvaluationResultDto> EvaluateLocalLicenseAsync(int terminalId = 0, string? expectedEnvironment = null);
    Task<bool> SaveSignedLicensePackageAsync(SignedLicensePackageDto package, string? expectedEnvironment = null);
    Task<LicenseOperationResponseDto> ActivateOnlineAsync(HttpClient http, int terminalId, string? licenseKey = null);
    Task<LicenseOperationResponseDto> ValidateOnlineAsync(HttpClient http, int terminalId, bool force = false);
}

public class ClientLicenseService : IClientLicenseService
{
    /// <summary>
    /// Enabled by default in POS.Client runtime (MauiProgram) and licensing tests.
    /// </summary>
    public static bool StrictEnforcementMode { get; set; } = false;

    /// <summary>
    /// Optional environment override for POS.Client ("Production" or "UAT").
    /// </summary>
    public static string ConfiguredClientEnvironment { get; set; } = LicenseEnvironmentConstants.Production;

    private static DateTime? _lastOnlineCheckAttemptUtc;
    private static int _consecutiveFailureCount;
    private const int MaxConsecutiveFailuresBeforeCooldown = 3;
    private static readonly TimeSpan PeriodicRevalidationInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FailureCooldownWindow = TimeSpan.FromMinutes(5);

    public event Action? OnLicenseStateChanged;
    public LicenseEvaluationResultDto? LastEvaluation { get; private set; }

    public static void ResetClientStateForTesting()
    {
        _lastOnlineCheckAttemptUtc = null;
        _consecutiveFailureCount = 0;
        ConfiguredClientEnvironment = LicenseEnvironmentConstants.Production;
    }

    public static CanonicalLicensePayload ToCanonicalPayload(LocalTerminalLicense local)
    {
        return new CanonicalLicensePayload
        {
            LicenseId = local.LicenseId,
            LicenseKey = local.LicenseKey,
            TerminalId = local.TerminalId,
            DeviceId = local.DeviceId,
            BranchId = local.BranchId,
            OutletId = local.OutletId,
            IssuedAtUtc = LicenseCryptoService.NormalizeUtcSecond(local.IssuedAtUtc),
            ActivatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(local.ActivatedAtUtc),
            ExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(local.ExpiresAtUtc),
            LicenseStatus = local.LicenseStatus,
            LicenseVersion = local.LicenseVersion,
            OfflineGracePeriodDays = local.OfflineGracePeriodDays > 0 ? local.OfflineGracePeriodDays : LicenseCryptoService.DefaultOfflineGracePeriodDays,
            EnvironmentType = LicenseCryptoService.NormalizeEnvironment(local.EnvironmentType),
            DurationValue = local.DurationValue > 0 ? local.DurationValue : LicenseCryptoService.DefaultLicenseDurationYears,
            DurationUnit = string.IsNullOrWhiteSpace(local.DurationUnit) ? LicenseDurationUnitConstants.Years : local.DurationUnit.Trim().ToUpperInvariant(),
            OriginalDurationValue = local.OriginalDurationValue > 0 ? local.OriginalDurationValue : (local.DurationValue > 0 ? local.DurationValue : LicenseCryptoService.DefaultLicenseDurationYears),
            OriginalDurationUnit = string.IsNullOrWhiteSpace(local.OriginalDurationUnit)
                ? (string.IsNullOrWhiteSpace(local.DurationUnit) ? LicenseDurationUnitConstants.Years : local.DurationUnit.Trim().ToUpperInvariant())
                : local.OriginalDurationUnit.Trim().ToUpperInvariant()
        };
    }

    public async Task<bool> SaveSignedLicensePackageAsync(SignedLicensePackageDto package, string? expectedEnvironment = null)
    {
        if (package?.Payload == null || string.IsNullOrWhiteSpace(package.Signature))
            return false;

        var targetEnv = string.IsNullOrWhiteSpace(expectedEnvironment)
            ? package.Payload.EnvironmentType
            : LicenseCryptoService.NormalizeEnvironment(expectedEnvironment);

        if (!LicenseCryptoService.VerifySignature(package.Payload, package.Signature, targetEnv))
            return false;

        var nowTick = BusinessDayClock.TickCount64();
        bool hasServerUtc = package.ServerUtcNow != default;
        var serverUtc = LicenseCryptoService.NormalizeUtcSecond(
            hasServerUtc ? package.ServerUtcNow : BusinessDayClock.UtcNow());
        var deviceUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var hwmCandidate = hasServerUtc ? serverUtc : (serverUtc > deviceUtc ? serverUtc : deviceUtc);

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            var p = package.Payload;
            var existing = await db.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == p.TerminalId);

            // Prevent replay / downgrade to an older LicenseVersion for the same LicenseId
            if (existing != null &&
                string.Equals(existing.LicenseId, p.LicenseId, StringComparison.OrdinalIgnoreCase) &&
                p.LicenseVersion < existing.LicenseVersion)
            {
                return false;
            }

            // Online server-verified responses re-anchor HighWaterMarkUtc to authoritative ServerUtcNow
            // so revalidating online recovers from stale/skewed client timestamps.
            DateTime mergedHwm = hwmCandidate;
            if (!hasServerUtc && existing?.HighWaterMarkUtc != null && existing.HighWaterMarkUtc.Value > mergedHwm)
            {
                mergedHwm = LicenseCryptoService.NormalizeUtcSecond(existing.HighWaterMarkUtc.Value);
            }

            if (existing == null)
            {
                existing = new LocalTerminalLicense
                {
                    TerminalId = p.TerminalId
                };
                db.TerminalLicenses.Add(existing);
            }

            existing.LicenseId = p.LicenseId;
            existing.LicenseKey = p.LicenseKey;
            existing.DeviceId = (p.DeviceId ?? string.Empty).Trim().ToUpperInvariant();
            existing.BranchId = p.BranchId;
            existing.OutletId = p.OutletId;
            existing.LicenseStatus = (p.LicenseStatus ?? LicenseStatusConstants.PendingActivation).Trim().ToUpperInvariant();
            existing.IssuedAtUtc = LicenseCryptoService.NormalizeUtcSecond(p.IssuedAtUtc);
            existing.ActivatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(p.ActivatedAtUtc);
            existing.ExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(p.ExpiresAtUtc);
            existing.LicenseVersion = p.LicenseVersion;
            existing.DurationValue = p.DurationValue > 0 ? p.DurationValue : LicenseCryptoService.DefaultLicenseDurationYears;
            existing.DurationUnit = string.IsNullOrWhiteSpace(p.DurationUnit) ? LicenseDurationUnitConstants.Years : p.DurationUnit.Trim().ToUpperInvariant();
            existing.OriginalDurationValue = p.OriginalDurationValue > 0 ? p.OriginalDurationValue : existing.DurationValue;
            existing.OriginalDurationUnit = string.IsNullOrWhiteSpace(p.OriginalDurationUnit) ? existing.DurationUnit : p.OriginalDurationUnit.Trim().ToUpperInvariant();
            existing.RenewalCount = package.RenewalCount;
            existing.LastRenewedAtUtc = LicenseCryptoService.NormalizeUtcSecond(package.LastRenewedAtUtc);
            existing.OfflineGracePeriodDays = p.OfflineGracePeriodDays > 0 ? p.OfflineGracePeriodDays : LicenseCryptoService.DefaultOfflineGracePeriodDays;
            existing.EnvironmentType = LicenseCryptoService.NormalizeEnvironment(p.EnvironmentType);
            existing.Signature = package.Signature.Trim();
            existing.LastValidatedServerUtc = serverUtc;
            existing.LastValidatedTickCount64 = nowTick;
            existing.HighWaterMarkUtc = mergedHwm;
            existing.HighWaterMarkTickCount64 = nowTick;
            existing.RevocationReason = package.RevocationReason;
            existing.RevokedAtUtc = LicenseCryptoService.NormalizeUtcSecond(package.RevokedAtUtc);
            existing.SuspensionReason = package.SuspensionReason;
            existing.SuspendedAtUtc = LicenseCryptoService.NormalizeUtcSecond(package.SuspendedAtUtc);
            existing.UpdatedAtUtc = serverUtc;

            var termCfg = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == p.TerminalId);
            if (termCfg != null)
            {
                termCfg.LastKnownServerUtc = serverUtc;
                termCfg.LastKnownServerTickCount64 = nowTick;
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        await EvaluateLocalLicenseAsync(package.Payload.TerminalId, expectedEnvironment);
        return true;
    }

    public async Task<LicenseEvaluationResultDto> EvaluateLocalLicenseAsync(int terminalId = 0, string? expectedEnvironment = null)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var nowTick = BusinessDayClock.TickCount64();

        LocalTerminalConfig? termCfg;
        LocalTerminalLicense? localLic;

        using (var db = new LocalPosDbContext())
        {
            db.InitializeDatabase();
            termCfg = terminalId > 0
                ? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                : await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();

            int resolvedTermId = termCfg?.TerminalId ?? terminalId;
            localLic = resolvedTermId > 0
                ? await db.TerminalLicenses.AsNoTracking().FirstOrDefaultAsync(l => l.TerminalId == resolvedTermId)
                : await db.TerminalLicenses.AsNoTracking().FirstOrDefaultAsync();
        }

        string? deviceUuid = termCfg?.DeviceUuid ?? termCfg?.TerminalIdentifier;
        string liveDeviceId = DeviceBindingHelper.ComputeDeviceId(deviceUuid);

        int expectedTermId = termCfg?.TerminalId ?? (terminalId > 0 ? terminalId : (localLic?.TerminalId ?? 0));
        int expectedBranchId = termCfg?.BranchId ?? (localLic?.BranchId ?? 0);
        int expectedOutletId = termCfg?.OutletId ?? (localLic?.OutletId ?? 0);

        if (localLic == null)
        {
            var missingRes = LicenseCryptoService.EvaluateLicense(
                payload: null,
                signatureBase64: null,
                expectedTerminalId: expectedTermId,
                expectedBranchId: expectedBranchId,
                expectedOutletId: expectedOutletId,
                currentDeviceId: liveDeviceId,
                expectedEnvironment: expectedEnvironment ?? ConfiguredClientEnvironment,
                deviceUtcNow: nowUtc,
                currentTickCount64: nowTick,
                lastValidatedServerUtc: null,
                lastValidatedTickCount64: null,
                highWaterMarkUtc: null);
            LastEvaluation = missingRes;
            OnLicenseStateChanged?.Invoke();
            return missingRes;
        }

        // Cross-check UTC high-water mark against TerminalConfig.LastKnownServerUtc
        // (Do not mix SyncOutboxes.CreatedAt here because outbox timestamps use device local time.)
        DateTime? referenceHwm = localLic.HighWaterMarkUtc;
        if (termCfg?.LastKnownServerUtc != null && (!referenceHwm.HasValue || termCfg.LastKnownServerUtc.Value > referenceHwm.Value))
        {
            referenceHwm = termCfg.LastKnownServerUtc.Value;
        }

        var payload = ToCanonicalPayload(localLic);
        var targetEnv = !string.IsNullOrWhiteSpace(expectedEnvironment)
            ? expectedEnvironment
            : (!string.IsNullOrWhiteSpace(ConfiguredClientEnvironment) ? ConfiguredClientEnvironment : payload.EnvironmentType);

        var eval = LicenseCryptoService.EvaluateLicense(
            payload: payload,
            signatureBase64: localLic.Signature,
            expectedTerminalId: expectedTermId,
            expectedBranchId: expectedBranchId,
            expectedOutletId: expectedOutletId,
            currentDeviceId: liveDeviceId,
            expectedEnvironment: targetEnv,
            deviceUtcNow: nowUtc,
            currentTickCount64: nowTick,
            lastValidatedServerUtc: localLic.LastValidatedServerUtc,
            lastValidatedTickCount64: localLic.LastValidatedTickCount64,
            highWaterMarkUtc: referenceHwm,
            isServerSideEvaluation: false);

        eval.RenewalCount = localLic.RenewalCount;
        eval.LastRenewedAtUtc = LicenseCryptoService.NormalizeUtcSecond(localLic.LastRenewedAtUtc);

        // Advance HighWaterMarkUtc monotonically when clock is valid and moving forward
        if (!eval.IsClockRollbackDetected && (!localLic.HighWaterMarkUtc.HasValue || nowUtc > localLic.HighWaterMarkUtc.Value.AddMinutes(1)))
        {
            try
            {
                await LocalPosDbContext.WriteLock.WaitAsync();
                try
                {
                    using var dbWrite = new LocalPosDbContext();
                    dbWrite.InitializeDatabase();
                    var tracked = await dbWrite.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == localLic.TerminalId);
                    if (tracked != null && (!tracked.HighWaterMarkUtc.HasValue || nowUtc > tracked.HighWaterMarkUtc.Value))
                    {
                        tracked.HighWaterMarkUtc = nowUtc;
                        tracked.HighWaterMarkTickCount64 = nowTick;
                        await dbWrite.SaveChangesAsync();
                    }
                }
                finally
                {
                    LocalPosDbContext.WriteLock.Release();
                }
            }
            catch { }
        }

        LastEvaluation = eval;
        OnLicenseStateChanged?.Invoke();
        return eval;
    }

    /// <summary>
    /// Static helper used by OfflineCheckoutService and ShiftSession at operational boundaries.
    /// Enforces license validation whenever a LocalTerminalLicense record exists or StrictEnforcementMode is enabled.
    /// </summary>
    public static async Task<(bool Allowed, string? ErrorMessage, LicenseEvaluationResultDto? Evaluation)> CheckOperationalGateAsync(int terminalId)
    {
        bool hasAnyLicenseRow;
        using (var db = new LocalPosDbContext())
        {
            db.InitializeDatabase();
            hasAnyLicenseRow = await db.TerminalLicenses.AsNoTracking().AnyAsync();
        }

        if (!hasAnyLicenseRow && !StrictEnforcementMode)
        {
            return (true, null, null);
        }

        var svc = new ClientLicenseService();
        var eval = await svc.EvaluateLocalLicenseAsync(terminalId);
        if (!eval.IsValidForNewSales)
        {
            return (false, eval.BlockMessage ?? $"Terminal license is {eval.EffectiveStatus}.", eval);
        }

        return (true, null, eval);
    }

    public async Task<LicenseOperationResponseDto> ActivateOnlineAsync(HttpClient http, int terminalId, string? licenseKey = null)
    {
        string deviceUuid;
        using (var db = new LocalPosDbContext())
        {
            db.InitializeDatabase();
            var cfg = terminalId > 0
                ? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                : await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            if (cfg == null)
            {
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = "TERMINAL_NOT_PROVISIONED",
                    Message = "Terminal is not provisioned locally. Complete device activation first."
                };
            }
            terminalId = cfg.TerminalId;
            deviceUuid = !string.IsNullOrWhiteSpace(cfg.DeviceUuid) ? cfg.DeviceUuid : cfg.TerminalIdentifier;
        }

        var liveDeviceId = DeviceBindingHelper.ComputeDeviceId(deviceUuid);
        var req = new LicenseActivateRequestDto
        {
            TerminalId = terminalId,
            DeviceUuid = deviceUuid,
            DeviceId = liveDeviceId,
            LicenseKey = string.IsNullOrWhiteSpace(licenseKey) ? null : licenseKey.Trim(),
            DeviceClockUtc = BusinessDayClock.UtcNow()
        };

        try
        {
            using var response = await http.PostAsJsonAsync("api/licensing/activate", req);
            var dto = await response.Content.ReadFromJsonAsync<LicenseOperationResponseDto>();
            if (dto?.LicensePackage != null)
            {
                await SaveSignedLicensePackageAsync(dto.LicensePackage);
            }
            if (dto != null)
            {
                _consecutiveFailureCount = 0;
                _lastOnlineCheckAttemptUtc = BusinessDayClock.UtcNow();
                return dto;
            }
            return new LicenseOperationResponseDto
            {
                Success = false,
                Message = $"Activation server returned {(int)response.StatusCode} {response.ReasonPhrase}."
            };
        }
        catch (Exception ex)
        {
            _consecutiveFailureCount++;
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "NETWORK_ERROR",
                Message = $"Could not reach licensing server: {ex.Message}"
            };
        }
    }

    public async Task<LicenseOperationResponseDto> ValidateOnlineAsync(HttpClient http, int terminalId, bool force = false)
    {
        var nowUtc = BusinessDayClock.UtcNow();
        if (!force && _lastOnlineCheckAttemptUtc.HasValue)
        {
            var elapsed = nowUtc - _lastOnlineCheckAttemptUtc.Value;
            if (_consecutiveFailureCount >= MaxConsecutiveFailuresBeforeCooldown && elapsed < FailureCooldownWindow)
            {
                var cachedEval = await EvaluateLocalLicenseAsync(terminalId);
                return new LicenseOperationResponseDto
                {
                    Success = cachedEval.IsValidForNewSales,
                    Message = "Using cached offline license validation during retry cooldown.",
                    Evaluation = cachedEval
                };
            }
            if (_consecutiveFailureCount == 0 && elapsed < PeriodicRevalidationInterval)
            {
                var cachedEval = await EvaluateLocalLicenseAsync(terminalId);
                return new LicenseOperationResponseDto
                {
                    Success = cachedEval.IsValidForNewSales,
                    Message = "License recently validated.",
                    Evaluation = cachedEval
                };
            }
        }

        string deviceUuid;
        int expectedBranchId;
        int expectedOutletId;
        string? licenseId = null;
        int? licenseVersion = null;

        using (var db = new LocalPosDbContext())
        {
            db.InitializeDatabase();
            var cfg = terminalId > 0
                ? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                : await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            if (cfg == null)
            {
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = "TERMINAL_NOT_PROVISIONED",
                    Message = "Terminal is not provisioned."
                };
            }
            terminalId = cfg.TerminalId;
            expectedBranchId = cfg.BranchId;
            expectedOutletId = cfg.OutletId;
            deviceUuid = !string.IsNullOrWhiteSpace(cfg.DeviceUuid) ? cfg.DeviceUuid : cfg.TerminalIdentifier;

            var lic = await db.TerminalLicenses.AsNoTracking().FirstOrDefaultAsync(l => l.TerminalId == terminalId);
            if (lic != null)
            {
                licenseId = lic.LicenseId;
                licenseVersion = lic.LicenseVersion;
            }
        }

        _lastOnlineCheckAttemptUtc = nowUtc;
        var liveDeviceId = DeviceBindingHelper.ComputeDeviceId(deviceUuid);

        var req = new LicenseValidateRequestDto
        {
            TerminalId = terminalId,
            DeviceUuid = deviceUuid,
            DeviceId = liveDeviceId,
            LicenseId = licenseId,
            ClientLicenseVersion = licenseVersion,
            DeviceClockUtc = nowUtc
        };

        try
        {
            using var response = await http.PostAsJsonAsync("api/licensing/validate", req);
            var dto = await response.Content.ReadFromJsonAsync<LicenseOperationResponseDto>();
            if (dto?.LicensePackage != null)
            {
                var incoming = dto.LicensePackage;
                var incomingEval = LicenseCryptoService.EvaluateLicense(
                    incoming.Payload,
                    incoming.Signature,
                    expectedTerminalId: terminalId,
                    expectedBranchId: expectedBranchId,
                    expectedOutletId: expectedOutletId,
                    currentDeviceId: liveDeviceId,
                    expectedEnvironment: ConfiguredClientEnvironment,
                    deviceUtcNow: incoming.ServerUtcNow != default ? incoming.ServerUtcNow : nowUtc,
                    currentTickCount64: BusinessDayClock.TickCount64(),
                    lastValidatedServerUtc: incoming.ServerUtcNow != default ? incoming.ServerUtcNow : nowUtc,
                    lastValidatedTickCount64: BusinessDayClock.TickCount64(),
                    highWaterMarkUtc: incoming.ServerUtcNow != default ? incoming.ServerUtcNow : nowUtc,
                    isServerSideEvaluation: true);

                if (!incomingEval.IsSignatureValid)
                {
                    _consecutiveFailureCount++;
                    var preservedEval = await EvaluateLocalLicenseAsync(terminalId);
                    return new LicenseOperationResponseDto
                    {
                        Success = false,
                        ErrorCode = "SIGNATURE_INVALID",
                        Message = "Server returned a license package that failed ECDSA P-256 signature verification. Cached license preserved.",
                        Evaluation = preservedEval
                    };
                }

                bool statusRevokedOrSuspended = string.Equals(incoming.Payload.LicenseStatus, LicenseStatusConstants.Revoked, StringComparison.OrdinalIgnoreCase)
                                                || string.Equals(incoming.Payload.LicenseStatus, LicenseStatusConstants.Suspended, StringComparison.OrdinalIgnoreCase);

                if (!statusRevokedOrSuspended && !incomingEval.IsDeviceBoundValid)
                {
                    _consecutiveFailureCount++;
                    var preservedEval = await EvaluateLocalLicenseAsync(terminalId);
                    return new LicenseOperationResponseDto
                    {
                        Success = false,
                        ErrorCode = incomingEval.BlockReasonCode ?? "DEVICE_MISMATCH",
                        Message = incomingEval.BlockMessage ?? "Server license package does not match this terminal's hardware device binding. Cached license preserved.",
                        Evaluation = preservedEval
                    };
                }

                await SaveSignedLicensePackageAsync(incoming);
                _consecutiveFailureCount = 0;
            }
            else
            {
                _consecutiveFailureCount++;
            }

            var updatedEval = await EvaluateLocalLicenseAsync(terminalId);
            if (dto != null)
            {
                dto.Evaluation = updatedEval;
                return dto;
            }

            return new LicenseOperationResponseDto
            {
                Success = updatedEval.IsValidForNewSales,
                Message = updatedEval.BlockMessage ?? updatedEval.WarningMessage ?? "License evaluated locally.",
                Evaluation = updatedEval
            };
        }
        catch (Exception ex)
        {
            _consecutiveFailureCount++;
            var fallbackEval = await EvaluateLocalLicenseAsync(terminalId);
            return new LicenseOperationResponseDto
            {
                Success = fallbackEval.IsValidForNewSales,
                ErrorCode = "OFFLINE_FALLBACK",
                Message = fallbackEval.IsValidForNewSales
                    ? $"Server unreachable ({ex.Message}). Operating under offline license grace period ({fallbackEval.RemainingOfflineGraceDays} day(s) remaining)."
                    : (fallbackEval.BlockMessage ?? "License validation required."),
                Evaluation = fallbackEval
            };
        }
    }
}
