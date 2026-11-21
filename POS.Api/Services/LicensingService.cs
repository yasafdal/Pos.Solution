using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Models;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Api.Services;

public interface ILicensingService
{
    Task<List<TerminalLicenseAdminDto>> GetLicensesAsync(int? branchId = null, int? outletId = null, int? terminalId = null, string? status = null, CancellationToken ct = default);
    Task<TerminalLicenseAdminDto?> GetLicenseByTerminalIdAsync(int terminalId, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> IssueLicenseAsync(IssueTerminalLicenseRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> ActivateLicenseAsync(LicenseActivateRequestDto request, string? ipAddress = null, CancellationToken ct = default);
    Task<SignedLicensePackageDto?> EnsureOrActivateTerminalLicenseAsync(Terminal terminal, string deviceUuid, string? explicitDeviceId, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> ValidateLicenseAsync(LicenseValidateRequestDto request, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> RenewLicenseAsync(RenewTerminalLicenseRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> SuspendLicenseAsync(ChangeLicenseStatusRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> ReactivateLicenseAsync(ChangeLicenseStatusRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> RevokeLicenseAsync(ChangeLicenseStatusRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> DeactivateDeviceAsync(ChangeLicenseStatusRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<LicenseOperationResponseDto> ReplaceDeviceAsync(ReplaceLicenseDeviceRequestDto request, string actor, string? ipAddress = null, CancellationToken ct = default);
    Task<(bool Allowed, string? ErrorMessage, LicenseEvaluationResultDto? Evaluation)> VerifyTerminalCanOperateAsync(int terminalId, string? deviceId = null, CancellationToken ct = default);
    Task<List<LicenseRenewalHistoryDto>> GetRenewalHistoryAsync(int? terminalId = null, CancellationToken ct = default);
    Task<List<LicenseDeviceHistoryDto>> GetDeviceHistoryAsync(int? terminalId = null, CancellationToken ct = default);
    Task<List<LicenseAuditLogDto>> GetAuditLogsAsync(int? terminalId = null, int limit = 100, CancellationToken ct = default);
    Task<List<ClientLicenseSummaryDto>> GetClientLicenseSummaryAsync(int organizationId, CancellationToken ct = default);
}

public class LicensingService : ILicensingService
{
    // Server-only ECDSA P-256 Private Scalar D values (NEVER present in POS.Shared, POS.Client, or PostgreSQL)
    private const string DefaultServerProdPrivateKeyD = "OnCDJURFDDNdUzRncWVreGEecraWE5u6T9QDaKlGIH8=";
    private const string DefaultServerUatPrivateKeyD = "OBTlDRINJNDW4Zd5ZRWvEeX3Xa7lTLZPR0FcK5QIYF8=";

    private static readonly ConcurrentDictionary<string, RateLimitBucket> RateLimitBuckets = new(StringComparer.OrdinalIgnoreCase);

    private readonly PosDbContext _context;
    private readonly IConfiguration? _configuration;

    public LicensingService(PosDbContext context, IConfiguration? configuration = null)
    {
        _context = context;
        _configuration = configuration;
    }

    private sealed class RateLimitBucket
    {
        public long WindowStartTicks;
        public int Count;
    }

    public static void ResetRateLimitsForTesting() => RateLimitBuckets.Clear();

    public static bool CheckRateLimit(string bucketKey, int maxRequestsPerMinute = 30)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var windowTicks = TimeSpan.FromMinutes(1).Ticks;
        var bucket = RateLimitBuckets.GetOrAdd(bucketKey, _ => new RateLimitBucket { WindowStartTicks = nowTicks, Count = 0 });
        lock (bucket)
        {
            if (nowTicks - bucket.WindowStartTicks >= windowTicks)
            {
                bucket.WindowStartTicks = nowTicks;
                bucket.Count = 1;
                return true;
            }
            bucket.Count++;
            return bucket.Count <= maxRequestsPerMinute;
        }
    }

    public ECParameters GetServerPrivateKeyParameters(string? environmentType)
    {
        var env = LicenseCryptoService.NormalizeEnvironment(environmentType);
        bool isUat = string.Equals(env, LicenseEnvironmentConstants.Uat, StringComparison.OrdinalIgnoreCase);
        var pubParams = LicenseCryptoService.GetPublicKeyParameters(env);

        string dBase64 = isUat
            ? (_configuration?["Licensing:UatPrivateKeyD"]
               ?? Environment.GetEnvironmentVariable("POS_LICENSING_UAT_PRIVATE_KEY_D")
               ?? DefaultServerUatPrivateKeyD)
            : (_configuration?["Licensing:ProductionPrivateKeyD"]
               ?? Environment.GetEnvironmentVariable("POS_LICENSING_PROD_PRIVATE_KEY_D")
               ?? DefaultServerProdPrivateKeyD);

        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = pubParams.Q,
            D = Convert.FromBase64String(dBase64.Trim())
        };
    }

    public string GetConfiguredServerEnvironment()
    {
        var cfgEnv = _configuration?["Licensing:EnvironmentType"]
                     ?? Environment.GetEnvironmentVariable("POS_LICENSING_ENVIRONMENT");
        return LicenseCryptoService.NormalizeEnvironment(cfgEnv);
    }

    public static CanonicalLicensePayload ToCanonicalPayload(TerminalLicense entity)
    {
        int durVal = entity.DurationValue > 0 ? entity.DurationValue : (entity.DurationYears > 0 ? entity.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears);
        string durUnit = string.IsNullOrWhiteSpace(entity.DurationUnit) ? LicenseDurationUnitConstants.Years : entity.DurationUnit.Trim().ToUpperInvariant();
        int origVal = entity.OriginalDurationValue > 0 ? entity.OriginalDurationValue : durVal;
        string origUnit = string.IsNullOrWhiteSpace(entity.OriginalDurationUnit) ? durUnit : entity.OriginalDurationUnit.Trim().ToUpperInvariant();

        return new CanonicalLicensePayload
        {
            LicenseId = entity.LicenseId,
            LicenseKey = entity.LicenseKey,
            TerminalId = entity.TerminalId,
            DeviceId = entity.DeviceId ?? string.Empty,
            BranchId = entity.BranchId,
            OutletId = entity.OutletId,
            IssuedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.IssuedAt),
            ActivatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.ActivatedAt),
            ExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.ExpiresAt),
            LicenseStatus = entity.LicenseStatus,
            LicenseVersion = entity.LicenseVersion,
            OfflineGracePeriodDays = entity.OfflineGracePeriodDays > 0 ? entity.OfflineGracePeriodDays : LicenseCryptoService.DefaultOfflineGracePeriodDays,
            EnvironmentType = LicenseCryptoService.NormalizeEnvironment(entity.EnvironmentType),
            DurationValue = durVal,
            DurationUnit = durUnit,
            OriginalDurationValue = origVal,
            OriginalDurationUnit = origUnit
        };
    }

    public void SignLicenseEntity(TerminalLicense entity)
    {
        entity.IssuedAt = LicenseCryptoService.NormalizeUtcSecond(entity.IssuedAt);
        entity.ActivatedAt = LicenseCryptoService.NormalizeUtcSecond(entity.ActivatedAt);
        entity.ExpiresAt = LicenseCryptoService.NormalizeUtcSecond(entity.ExpiresAt);
        entity.EnvironmentType = LicenseCryptoService.NormalizeEnvironment(entity.EnvironmentType);
        entity.DeviceId = (entity.DeviceId ?? string.Empty).Trim().ToUpperInvariant();
        entity.LicenseStatus = (entity.LicenseStatus ?? LicenseStatusConstants.PendingActivation).Trim().ToUpperInvariant();
        if (entity.DurationValue <= 0)
            entity.DurationValue = entity.DurationYears > 0 ? entity.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears;
        if (string.IsNullOrWhiteSpace(entity.DurationUnit))
            entity.DurationUnit = LicenseDurationUnitConstants.Years;
        else
            entity.DurationUnit = entity.DurationUnit.Trim().ToUpperInvariant();
        if (entity.OriginalDurationValue <= 0)
            entity.OriginalDurationValue = entity.DurationValue;
        if (string.IsNullOrWhiteSpace(entity.OriginalDurationUnit))
            entity.OriginalDurationUnit = entity.DurationUnit;
        else
            entity.OriginalDurationUnit = entity.OriginalDurationUnit.Trim().ToUpperInvariant();
        entity.DurationYears = LicenseCryptoService.ApproximateDurationYears(entity.DurationValue, entity.DurationUnit);
        if (entity.OfflineGracePeriodDays <= 0)
            entity.OfflineGracePeriodDays = LicenseCryptoService.DefaultOfflineGracePeriodDays;

        var payload = ToCanonicalPayload(entity);
        var privParams = GetServerPrivateKeyParameters(entity.EnvironmentType);
        entity.Signature = LicenseCryptoService.SignPayload(payload, privParams);
    }

    public static SignedLicensePackageDto ToSignedPackageDto(TerminalLicense entity, Terminal? terminal, DateTime serverUtcNow)
    {
        return new SignedLicensePackageDto
        {
            Payload = ToCanonicalPayload(entity),
            Signature = entity.Signature,
            ServerUtcNow = LicenseCryptoService.NormalizeUtcSecond(serverUtcNow),
            TerminalCode = terminal?.TerminalCode,
            TerminalName = terminal?.Name,
            BranchName = terminal?.Branch?.Name,
            OutletName = terminal?.Outlet?.Name,
            RevocationReason = entity.RevocationReason,
            RevokedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.RevokedAt),
            SuspensionReason = entity.SuspensionReason,
            SuspendedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.SuspendedAt),
            RenewalCount = entity.RenewalCount,
            LastRenewedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.LastRenewedAt)
        };
    }

    public static TerminalLicenseAdminDto ToAdminDto(TerminalLicense entity, Terminal? terminal, DateTime serverUtcNow)
    {
        var payload = ToCanonicalPayload(entity);
        var eval = LicenseCryptoService.EvaluateLicense(
            payload: payload,
            signatureBase64: entity.Signature,
            expectedTerminalId: entity.TerminalId,
            expectedBranchId: entity.BranchId,
            expectedOutletId: entity.OutletId,
            currentDeviceId: entity.DeviceId,
            expectedEnvironment: entity.EnvironmentType,
            deviceUtcNow: serverUtcNow,
            currentTickCount64: BusinessDayClock.TickCount64(),
            lastValidatedServerUtc: entity.LastValidatedAt ?? serverUtcNow,
            lastValidatedTickCount64: null,
            highWaterMarkUtc: serverUtcNow,
            isServerSideEvaluation: true);

        return new TerminalLicenseAdminDto
        {
            LicenseId = entity.LicenseId,
            LicenseKey = entity.LicenseKey,
            TerminalId = entity.TerminalId,
            TerminalCode = terminal?.TerminalCode ?? $"T-{entity.TerminalId}",
            TerminalName = terminal?.Name ?? $"Terminal #{entity.TerminalId}",
            BranchId = entity.BranchId,
            BranchName = terminal?.Branch?.Name ?? $"Branch #{entity.BranchId}",
            OutletId = entity.OutletId,
            OutletName = terminal?.Outlet?.Name ?? $"Outlet #{entity.OutletId}",
            DeviceId = entity.DeviceId,
            TerminalUuid = terminal?.Uuid,
            LicenseStatus = entity.LicenseStatus,
            EffectiveStatus = eval.EffectiveStatus,
            IsSignatureValid = eval.IsSignatureValid,
            DurationYears = entity.DurationYears > 0 ? entity.DurationYears : LicenseCryptoService.ApproximateDurationYears(payload.DurationValue, payload.DurationUnit),
            DurationValue = payload.DurationValue,
            DurationUnit = payload.DurationUnit,
            OriginalDurationValue = payload.OriginalDurationValue,
            OriginalDurationUnit = payload.OriginalDurationUnit,
            RenewalCount = entity.RenewalCount,
            LastRenewedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.LastRenewedAt),
            IssuedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.IssuedAt),
            ActivatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.ActivatedAt),
            ExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.ExpiresAt),
            LastValidatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.LastValidatedAt),
            RemainingDays = eval.RemainingDays,
            WarningThresholdDays = eval.WarningThresholdDays,
            LicenseVersion = entity.LicenseVersion,
            OfflineGracePeriodDays = entity.OfflineGracePeriodDays,
            EnvironmentType = entity.EnvironmentType,
            RevokedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.RevokedAt),
            RevocationReason = entity.RevocationReason,
            SuspendedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.SuspendedAt),
            SuspensionReason = entity.SuspensionReason,
            IssuedBy = entity.IssuedBy,
            CreatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.CreatedAt),
            UpdatedAtUtc = LicenseCryptoService.NormalizeUtcSecond(entity.UpdatedAt)
        };
    }

    private void RecordAudit(
        TerminalLicense? lic,
        int? terminalId,
        int? branchId,
        int? outletId,
        string? deviceId,
        string eventType,
        string eventStatus,
        string message,
        string? actor,
        string? ipAddress,
        DateTime serverUtcNow,
        DateTime? deviceClockUtc = null)
    {
        int? skewSeconds = null;
        if (deviceClockUtc.HasValue)
        {
            var normDev = LicenseCryptoService.NormalizeUtcSecond(deviceClockUtc.Value);
            skewSeconds = (int)Math.Round((normDev - serverUtcNow).TotalSeconds);
        }

        _context.LicenseAuditLogs.Add(new LicenseAuditLog
        {
            LicenseId = lic?.LicenseId,
            TerminalId = terminalId ?? lic?.TerminalId,
            BranchId = branchId ?? lic?.BranchId,
            OutletId = outletId ?? lic?.OutletId,
            DeviceId = deviceId ?? lic?.DeviceId,
            EventType = eventType,
            EventStatus = eventStatus,
            Message = message.Length > 500 ? message[..500] : message,
            Actor = actor,
            IpAddress = ipAddress,
            DeviceClockUtc = deviceClockUtc.HasValue ? LicenseCryptoService.NormalizeUtcSecond(deviceClockUtc.Value) : null,
            ServerClockUtc = serverUtcNow,
            ClockSkewSeconds = skewSeconds,
            OccurredAt = serverUtcNow
        });
    }

    public async Task<List<TerminalLicenseAdminDto>> GetLicensesAsync(
        int? branchId = null,
        int? outletId = null,
        int? terminalId = null,
        string? status = null,
        CancellationToken ct = default)
    {
        var serverUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminalsQuery = _context.Terminals
            .AsNoTracking()
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .AsQueryable();

        if (branchId.HasValue && branchId.Value > 0)
            terminalsQuery = terminalsQuery.Where(t => t.BranchId == branchId.Value);
        if (outletId.HasValue && outletId.Value > 0)
            terminalsQuery = terminalsQuery.Where(t => t.OutletId == outletId.Value);
        if (terminalId.HasValue && terminalId.Value > 0)
            terminalsQuery = terminalsQuery.Where(t => t.TerminalId == terminalId.Value);

        var terminals = await terminalsQuery.OrderBy(t => t.BranchId).ThenBy(t => t.OutletId).ThenBy(t => t.TerminalId).ToListAsync(ct);
        var termIds = terminals.Select(t => t.TerminalId).ToList();

        var licenses = await _context.TerminalLicenses
            .AsNoTracking()
            .Where(l => termIds.Contains(l.TerminalId))
            .ToDictionaryAsync(l => l.TerminalId, ct);

        var list = new List<TerminalLicenseAdminDto>();
        foreach (var term in terminals)
        {
            if (licenses.TryGetValue(term.TerminalId, out var lic))
            {
                list.Add(ToAdminDto(lic, term, serverUtc));
            }
            else
            {
                list.Add(new TerminalLicenseAdminDto
                {
                    LicenseId = string.Empty,
                    LicenseKey = "UNISSUED",
                    TerminalId = term.TerminalId,
                    TerminalCode = term.TerminalCode,
                    TerminalName = term.Name,
                    BranchId = term.BranchId,
                    BranchName = term.Branch?.Name ?? string.Empty,
                    OutletId = term.OutletId,
                    OutletName = term.Outlet?.Name ?? string.Empty,
                    DeviceId = null,
                    TerminalUuid = term.Uuid,
                    LicenseStatus = LicenseStatusConstants.PendingActivation,
                    EffectiveStatus = LicenseStatusConstants.PendingActivation,
                    IsSignatureValid = true,
                    DurationYears = LicenseCryptoService.DefaultLicenseDurationYears,
                    DurationValue = LicenseCryptoService.DefaultLicenseDurationYears,
                    DurationUnit = LicenseDurationUnitConstants.Years,
                    OriginalDurationValue = LicenseCryptoService.DefaultLicenseDurationYears,
                    OriginalDurationUnit = LicenseDurationUnitConstants.Years,
                    RenewalCount = 0,
                    OfflineGracePeriodDays = LicenseCryptoService.DefaultOfflineGracePeriodDays,
                    EnvironmentType = GetConfiguredServerEnvironment(),
                    CreatedAtUtc = term.CreatedAt,
                    UpdatedAtUtc = term.UpdatedAt
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statusTrim = status.Trim().ToUpperInvariant();
            list = list.Where(x =>
                string.Equals(x.EffectiveStatus, statusTrim, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.LicenseStatus, statusTrim, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return list;
    }

    public async Task<TerminalLicenseAdminDto?> GetLicenseByTerminalIdAsync(int terminalId, CancellationToken ct = default)
    {
        var items = await GetLicensesAsync(terminalId: terminalId, ct: ct);
        return items.FirstOrDefault();
    }

    public async Task<LicenseOperationResponseDto> IssueLicenseAsync(
        IssueTerminalLicenseRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null)
        {
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "TERMINAL_NOT_FOUND",
                Message = "Terminal not found.",
                ServerUtcNow = nowUtc
            };
        }

        if (!LicenseCryptoService.TryNormalizeDuration(
                request.DurationValue,
                request.DurationUnit,
                request.DurationYears,
                out int durationValue,
                out string durationUnit,
                out string? durationError))
        {
            RecordAudit(null, terminal.TerminalId, terminal.BranchId, terminal.OutletId, null,
                "ISSUE_REJECTED", "BLOCKED", durationError ?? "Invalid license duration.", actor, ipAddress, nowUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "INVALID_DURATION",
                Message = durationError ?? "Invalid license duration.",
                ServerUtcNow = nowUtc
            };
        }

        int durationYears = LicenseCryptoService.ApproximateDurationYears(durationValue, durationUnit);
        string durationLabel = LicenseCryptoService.FormatDurationLabel(durationValue, durationUnit);
        int graceDays = request.OfflineGracePeriodDays > 0 ? request.OfflineGracePeriodDays : LicenseCryptoService.DefaultOfflineGracePeriodDays;
        string envType = LicenseCryptoService.NormalizeEnvironment(
            string.IsNullOrWhiteSpace(request.EnvironmentType) ? GetConfiguredServerEnvironment() : request.EnvironmentType);

        var existing = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
        string boundDeviceId = !string.IsNullOrWhiteSpace(existing?.DeviceId)
            ? existing.DeviceId
            : (!string.IsNullOrWhiteSpace(terminal.Uuid) && terminal.IsActivationCodeUsed
                ? DeviceBindingHelper.ComputeDeviceId(terminal.Uuid)
                : string.Empty);

        bool activateNow = request.ActivateImmediatelyIfBound && !string.IsNullOrWhiteSpace(boundDeviceId);
        DateTime activationAnchorUtc = request.ActivationEffectiveAtUtc.HasValue
            ? LicenseCryptoService.NormalizeUtcSecond(request.ActivationEffectiveAtUtc.Value)
            : nowUtc;

        if (existing == null)
        {
            existing = new TerminalLicense
            {
                LicenseId = Guid.NewGuid().ToString("N"),
                LicenseKey = LicenseCryptoService.GenerateLicenseKey(terminal.BranchId, terminal.OutletId, terminal.TerminalId),
                TerminalId = terminal.TerminalId,
                BranchId = terminal.BranchId,
                OutletId = terminal.OutletId,
                DeviceId = activateNow ? boundDeviceId : string.Empty,
                LicenseStatus = activateNow ? LicenseStatusConstants.Active : LicenseStatusConstants.PendingActivation,
                DurationYears = durationYears,
                DurationValue = durationValue,
                DurationUnit = durationUnit,
                OriginalDurationValue = durationValue,
                OriginalDurationUnit = durationUnit,
                RenewalCount = 0,
                IssuedAt = nowUtc,
                ActivatedAt = activateNow ? activationAnchorUtc : null,
                ExpiresAt = activateNow ? LicenseCryptoService.CalculateExpirationUtc(activationAnchorUtc, durationValue, durationUnit) : null,
                LastValidatedAt = activateNow ? nowUtc : null,
                LicenseVersion = 1,
                OfflineGracePeriodDays = graceDays,
                EnvironmentType = envType,
                IssuedBy = actor,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };
            SignLicenseEntity(existing);
            _context.TerminalLicenses.Add(existing);
        }
        else
        {
            existing.BranchId = terminal.BranchId;
            existing.OutletId = terminal.OutletId;
            existing.DurationYears = durationYears;
            existing.DurationValue = durationValue;
            existing.DurationUnit = durationUnit;
            existing.OriginalDurationValue = durationValue;
            existing.OriginalDurationUnit = durationUnit;
            existing.OfflineGracePeriodDays = graceDays;
            existing.EnvironmentType = envType;
            existing.IssuedAt = nowUtc;
            existing.RevokedAt = null;
            existing.RevocationReason = null;
            existing.SuspendedAt = null;
            existing.SuspensionReason = null;
            existing.IssuedBy = actor;
            existing.LicenseVersion++;
            if (activateNow)
            {
                existing.DeviceId = boundDeviceId;
                existing.LicenseStatus = LicenseStatusConstants.Active;
                existing.ActivatedAt = request.ActivationEffectiveAtUtc.HasValue
                    ? activationAnchorUtc
                    : (existing.ActivatedAt ?? activationAnchorUtc);
                existing.ExpiresAt = LicenseCryptoService.CalculateExpirationUtc(existing.ActivatedAt.Value, durationValue, durationUnit);
                existing.LastValidatedAt = nowUtc;
            }
            else
            {
                existing.LicenseStatus = LicenseStatusConstants.PendingActivation;
                existing.ActivatedAt = null;
                existing.ExpiresAt = null;
            }
            existing.UpdatedAt = nowUtc;
            SignLicenseEntity(existing);
        }

        RecordAudit(
            existing,
            terminal.TerminalId,
            terminal.BranchId,
            terminal.OutletId,
            existing.DeviceId,
            "ISSUED",
            "SUCCESS",
            $"Issued {durationLabel} {envType} license ({existing.LicenseKey}) for terminal {terminal.TerminalCode}. Status={existing.LicenseStatus}.",
            actor,
            ipAddress,
            nowUtc);

        await _context.SaveChangesAsync(ct);

        var pkg = ToSignedPackageDto(existing, terminal, nowUtc);
        var eval = LicenseCryptoService.EvaluateLicense(
            pkg.Payload, pkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
            existing.DeviceId, existing.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);

        return new LicenseOperationResponseDto
        {
            Success = true,
            Message = $"{durationLabel} license {existing.LicenseKey} issued for terminal {terminal.TerminalCode}.",
            LicensePackage = pkg,
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<SignedLicensePackageDto?> EnsureOrActivateTerminalLicenseAsync(
        Terminal terminal,
        string deviceUuid,
        string? explicitDeviceId,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        string deviceId = !string.IsNullOrWhiteSpace(explicitDeviceId)
            ? explicitDeviceId.Trim().ToUpperInvariant()
            : DeviceBindingHelper.ComputeDeviceId(deviceUuid);

        var existing = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
        if (existing == null)
        {
            int defaultYears = LicenseCryptoService.DefaultLicenseDurationYears;
            if (int.TryParse(_configuration?["Licensing:DefaultDurationYears"], out int cfgYears) && cfgYears > 0 && cfgYears <= 50)
            {
                defaultYears = cfgYears;
            }
            string envType = GetConfiguredServerEnvironment();
            string durLabel = LicenseCryptoService.FormatDurationLabel(defaultYears, LicenseDurationUnitConstants.Years);
            existing = new TerminalLicense
            {
                LicenseId = Guid.NewGuid().ToString("N"),
                LicenseKey = LicenseCryptoService.GenerateLicenseKey(terminal.BranchId, terminal.OutletId, terminal.TerminalId),
                TerminalId = terminal.TerminalId,
                BranchId = terminal.BranchId,
                OutletId = terminal.OutletId,
                DeviceId = deviceId,
                LicenseStatus = LicenseStatusConstants.Active,
                DurationYears = defaultYears,
                DurationValue = defaultYears,
                DurationUnit = LicenseDurationUnitConstants.Years,
                OriginalDurationValue = defaultYears,
                OriginalDurationUnit = LicenseDurationUnitConstants.Years,
                RenewalCount = 0,
                IssuedAt = nowUtc,
                ActivatedAt = nowUtc,
                ExpiresAt = LicenseCryptoService.CalculateExpirationUtc(nowUtc, defaultYears, LicenseDurationUnitConstants.Years),
                LastValidatedAt = nowUtc,
                LicenseVersion = 1,
                OfflineGracePeriodDays = LicenseCryptoService.DefaultOfflineGracePeriodDays,
                EnvironmentType = envType,
                IssuedBy = actor,
                CreatedAt = nowUtc,
                UpdatedAt = nowUtc
            };
            SignLicenseEntity(existing);
            _context.TerminalLicenses.Add(existing);

            _context.LicenseDeviceHistories.Add(new LicenseDeviceHistory
            {
                LicenseId = existing.LicenseId,
                TerminalId = terminal.TerminalId,
                OldDeviceId = null,
                NewDeviceId = deviceId,
                ActionType = "INITIAL_BINDING",
                AuthorizedBy = actor,
                Reason = $"Initial {durLabel} terminal activation binding",
                OccurredAt = nowUtc
            });

            RecordAudit(
                existing,
                terminal.TerminalId,
                terminal.BranchId,
                terminal.OutletId,
                deviceId,
                "ACTIVATED",
                "SUCCESS",
                $"Activated {durLabel} license {existing.LicenseKey} until {existing.ExpiresAt:yyyy-MM-dd} on device {deviceId}.",
                actor,
                ipAddress,
                nowUtc);

            await _context.SaveChangesAsync(ct);
            return ToSignedPackageDto(existing, terminal, nowUtc);
        }

        // Verify PostgreSQL record integrity before modifying or trusting it
        if (!LicenseCryptoService.VerifySignature(ToCanonicalPayload(existing), existing.Signature))
        {
            RecordAudit(
                existing,
                terminal.TerminalId,
                terminal.BranchId,
                terminal.OutletId,
                deviceId,
                "TAMPER_DETECTED",
                "BLOCKED",
                "Database tamper detected on TerminalLicenses table during activation: signature verification failed.",
                actor,
                ipAddress,
                nowUtc);
            await _context.SaveChangesAsync(ct);
            return null;
        }

        var statusUpper = (existing.LicenseStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (statusUpper == LicenseStatusConstants.Revoked || statusUpper == LicenseStatusConstants.Suspended)
        {
            return ToSignedPackageDto(existing, terminal, nowUtc);
        }

        string oldDeviceId = existing.DeviceId;
        bool deviceChanged = !string.Equals(oldDeviceId, deviceId, StringComparison.OrdinalIgnoreCase);

        existing.BranchId = terminal.BranchId;
        existing.OutletId = terminal.OutletId;
        existing.DeviceId = deviceId;
        existing.LicenseStatus = LicenseStatusConstants.Active;
        int durVal = existing.DurationValue > 0 ? existing.DurationValue : (existing.DurationYears > 0 ? existing.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears);
        string durUnit = string.IsNullOrWhiteSpace(existing.DurationUnit) ? LicenseDurationUnitConstants.Years : existing.DurationUnit;
        if (!existing.ActivatedAt.HasValue)
        {
            existing.ActivatedAt = nowUtc;
            existing.ExpiresAt = LicenseCryptoService.CalculateExpirationUtc(nowUtc, durVal, durUnit);
        }
        else if (!existing.ExpiresAt.HasValue)
        {
            existing.ExpiresAt = LicenseCryptoService.CalculateExpirationUtc(existing.ActivatedAt.Value, durVal, durUnit);
        }
        existing.LastValidatedAt = nowUtc;
        if (deviceChanged || statusUpper == LicenseStatusConstants.PendingActivation)
        {
            existing.LicenseVersion++;
        }
        existing.UpdatedAt = nowUtc;
        SignLicenseEntity(existing);

        if (deviceChanged)
        {
            _context.LicenseDeviceHistories.Add(new LicenseDeviceHistory
            {
                LicenseId = existing.LicenseId,
                TerminalId = terminal.TerminalId,
                OldDeviceId = string.IsNullOrWhiteSpace(oldDeviceId) ? null : oldDeviceId,
                NewDeviceId = deviceId,
                ActionType = string.IsNullOrWhiteSpace(oldDeviceId) ? "INITIAL_BINDING" : "RE_ACTIVATION_BINDING",
                AuthorizedBy = actor,
                Reason = "Terminal activation code pairing",
                OccurredAt = nowUtc
            });
        }

        RecordAudit(
            existing,
            terminal.TerminalId,
            terminal.BranchId,
            terminal.OutletId,
            deviceId,
            "ACTIVATED",
            "SUCCESS",
            $"Terminal license {existing.LicenseKey} activated/paired on device {deviceId} (Expires {existing.ExpiresAt:yyyy-MM-dd}).",
            actor,
            ipAddress,
            nowUtc);

        await _context.SaveChangesAsync(ct);
        return ToSignedPackageDto(existing, terminal, nowUtc);
    }

    public async Task<LicenseOperationResponseDto> ActivateLicenseAsync(
        LicenseActivateRequestDto request,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        string rateKey = $"ACTIVATE|{ipAddress ?? "local"}|{request.TerminalId}";
        if (!CheckRateLimit(rateKey, maxRequestsPerMinute: 20))
        {
            RecordAudit(null, request.TerminalId, null, null, request.DeviceId,
                "RATE_LIMITED", "BLOCKED", "Too many license activation attempts.", "Terminal", ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "RATE_LIMITED",
                Message = "Too many activation attempts. Please wait a minute before retrying.",
                ServerUtcNow = nowUtc
            };
        }

        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Branch.IsActive)
        {
            RecordAudit(null, request.TerminalId, null, null, request.DeviceId,
                "UNAUTHORIZED_ATTEMPT", "BLOCKED", "Activation requested for non-existent or inactive terminal/outlet/branch.", "Terminal", ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "TERMINAL_INACTIVE",
                Message = "Terminal, outlet, or branch is not registered or is inactive.",
                ServerUtcNow = nowUtc
            };
        }

        string cleanUuid = (request.DeviceUuid ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanUuid) ||
            (!string.IsNullOrWhiteSpace(terminal.Uuid) && !string.Equals(terminal.Uuid, cleanUuid, StringComparison.OrdinalIgnoreCase)))
        {
            RecordAudit(null, terminal.TerminalId, terminal.BranchId, terminal.OutletId, request.DeviceId,
                "UNAUTHORIZED_ATTEMPT", "BLOCKED", $"DeviceUuid mismatch during license activation for terminal {terminal.TerminalCode}.", terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "DEVICE_UUID_MISMATCH",
                Message = "Device UUID does not match the registered terminal pairing.",
                ServerUtcNow = nowUtc
            };
        }

        string incomingDeviceId = !string.IsNullOrWhiteSpace(request.DeviceId)
            ? request.DeviceId.Trim().ToUpperInvariant()
            : DeviceBindingHelper.ComputeDeviceId(cleanUuid);

        var existing = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
        if (existing != null)
        {
            if (!LicenseCryptoService.VerifySignature(ToCanonicalPayload(existing), existing.Signature))
            {
                RecordAudit(existing, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                    "TAMPER_DETECTED", "BLOCKED", "PostgreSQL TerminalLicenses signature verification failed during activation.", terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
                await _context.SaveChangesAsync(ct);
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = "SIGNATURE_INVALID",
                    Message = "Server license record integrity check failed (database signature mismatch).",
                    ServerUtcNow = nowUtc
                };
            }

            if (!string.IsNullOrWhiteSpace(request.LicenseKey) &&
                !string.Equals(existing.LicenseKey, request.LicenseKey.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                RecordAudit(existing, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                    "UNAUTHORIZED_ATTEMPT", "BLOCKED", "Invalid LicenseKey supplied during activation.", terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
                await _context.SaveChangesAsync(ct);
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = "INVALID_LICENSE_KEY",
                    Message = "The provided License Key does not match this terminal.",
                    ServerUtcNow = nowUtc
                };
            }

            // Prevent binding to a different physical device if already bound unless Admin cleared/replaced it
            if (!string.IsNullOrWhiteSpace(existing.DeviceId) &&
                !string.Equals(existing.DeviceId, incomingDeviceId, StringComparison.OrdinalIgnoreCase) &&
                existing.LicenseStatus != LicenseStatusConstants.PendingActivation)
            {
                RecordAudit(existing, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                    "UNAUTHORIZED_ATTEMPT", "BLOCKED", $"Activation blocked: license is bound to {existing.DeviceId}, attempted from {incomingDeviceId}.", terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
                await _context.SaveChangesAsync(ct);
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = "DEVICE_ALREADY_BOUND",
                    Message = "License is already bound to another physical device. An Administrator must authorize device replacement in POS.Web.",
                    ServerUtcNow = nowUtc
                };
            }

            var statusUpper = (existing.LicenseStatus ?? string.Empty).Trim().ToUpperInvariant();
            if (statusUpper == LicenseStatusConstants.Revoked || statusUpper == LicenseStatusConstants.Suspended)
            {
                var blockedPkg = ToSignedPackageDto(existing, terminal, nowUtc);
                var blockedEval = LicenseCryptoService.EvaluateLicense(
                    blockedPkg.Payload, blockedPkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
                    incomingDeviceId, existing.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);
                return new LicenseOperationResponseDto
                {
                    Success = false,
                    ErrorCode = blockedEval.BlockReasonCode,
                    Message = blockedEval.BlockMessage ?? $"License is {statusUpper}.",
                    LicensePackage = blockedPkg,
                    Evaluation = blockedEval,
                    ServerUtcNow = nowUtc
                };
            }
        }

        var pkg = await EnsureOrActivateTerminalLicenseAsync(terminal, cleanUuid, incomingDeviceId, terminal.TerminalCode, ipAddress, ct);
        if (pkg == null)
        {
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "SIGNATURE_INVALID",
                Message = "License activation failed due to database signature integrity check.",
                ServerUtcNow = nowUtc
            };
        }

        var eval = LicenseCryptoService.EvaluateLicense(
            pkg.Payload, pkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
            incomingDeviceId, pkg.Payload.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);

        return new LicenseOperationResponseDto
        {
            Success = eval.IsValidForNewSales,
            ErrorCode = eval.BlockReasonCode,
            Message = eval.IsValidForNewSales
                ? $"Terminal license {pkg.Payload.LicenseKey} active until {pkg.Payload.ExpiresAtUtc:yyyy-MM-dd}."
                : (eval.BlockMessage ?? "License is not valid for sales."),
            LicensePackage = pkg,
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<LicenseOperationResponseDto> ValidateLicenseAsync(
        LicenseValidateRequestDto request,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        string rateKey = $"VALIDATE|{ipAddress ?? "local"}|{request.TerminalId}";
        if (!CheckRateLimit(rateKey, maxRequestsPerMinute: 60))
        {
            RecordAudit(null, request.TerminalId, null, null, request.DeviceId,
                "RATE_LIMITED", "BLOCKED", "Too many license validation requests.", "Terminal", ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "RATE_LIMITED",
                Message = "Too many validation requests. Please wait before retrying.",
                ServerUtcNow = nowUtc
            };
        }

        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Branch.IsActive)
        {
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "TERMINAL_INACTIVE",
                Message = "Terminal, outlet, or branch is inactive or not found.",
                ServerUtcNow = nowUtc
            };
        }

        string incomingDeviceId = !string.IsNullOrWhiteSpace(request.DeviceId)
            ? request.DeviceId.Trim().ToUpperInvariant()
            : DeviceBindingHelper.ComputeDeviceId(request.DeviceUuid);

        var lic = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
        if (lic == null)
        {
            // Controlled migration path for existing paired terminals
            if (!string.IsNullOrWhiteSpace(terminal.Uuid) &&
                string.Equals(terminal.Uuid, request.DeviceUuid?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                var migratedPkg = await EnsureOrActivateTerminalLicenseAsync(terminal, terminal.Uuid, incomingDeviceId, "AutoMigration", ipAddress, ct);
                if (migratedPkg != null)
                {
                    var migEval = LicenseCryptoService.EvaluateLicense(
                        migratedPkg.Payload, migratedPkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
                        incomingDeviceId, migratedPkg.Payload.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);
                    return new LicenseOperationResponseDto
                    {
                        Success = migEval.IsValidForNewSales,
                        Message = "Terminal license provisioned and validated.",
                        LicensePackage = migratedPkg,
                        Evaluation = migEval,
                        ServerUtcNow = nowUtc
                    };
                }
            }

            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "LICENSE_MISSING",
                Message = "No license has been issued for this terminal.",
                ServerUtcNow = nowUtc
            };
        }

        // Verify PostgreSQL signature before trusting the DB row
        var payload = ToCanonicalPayload(lic);
        if (!LicenseCryptoService.VerifySignature(payload, lic.Signature))
        {
            RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                "TAMPER_DETECTED", "BLOCKED", "PostgreSQL TerminalLicenses row failed digital signature verification during online validation!", terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);

            var tamperEval = new LicenseEvaluationResultDto
            {
                IsValidForNewSales = false,
                IsValidForNewShift = false,
                EffectiveStatus = LicenseStatusConstants.ValidationRequired,
                StoredStatus = lic.LicenseStatus,
                IsSignatureValid = false,
                BlockReasonCode = "SIGNATURE_INVALID",
                BlockMessage = "Central database license record failed digital signature verification (tampering detected).",
                EvaluatedAtUtc = nowUtc,
                TerminalId = terminal.TerminalId,
                BranchId = terminal.BranchId,
                OutletId = terminal.OutletId
            };

            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "SIGNATURE_INVALID",
                Message = tamperEval.BlockMessage,
                Evaluation = tamperEval,
                ServerUtcNow = nowUtc
            };
        }

        // Detect suspicious device clock skew / rollback reported by client
        if (request.DeviceClockUtc.HasValue)
        {
            var devUtc = LicenseCryptoService.NormalizeUtcSecond(request.DeviceClockUtc.Value);
            var skew = (devUtc - nowUtc).TotalMinutes;
            if (skew < -10)
            {
                RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                    "CLOCK_ROLLBACK_DETECTED", "WARNING",
                    $"Client device clock ({devUtc:yyyy-MM-dd HH:mm} UTC) is behind server UTC ({nowUtc:yyyy-MM-dd HH:mm} UTC) by {Math.Abs(skew):F1} minutes.",
                    terminal.TerminalCode, ipAddress, nowUtc, devUtc);
            }
        }

        var eval = LicenseCryptoService.EvaluateLicense(
            payload: payload,
            signatureBase64: lic.Signature,
            expectedTerminalId: terminal.TerminalId,
            expectedBranchId: terminal.BranchId,
            expectedOutletId: terminal.OutletId,
            currentDeviceId: incomingDeviceId,
            expectedEnvironment: lic.EnvironmentType,
            deviceUtcNow: nowUtc,
            currentTickCount64: BusinessDayClock.TickCount64(),
            lastValidatedServerUtc: nowUtc,
            lastValidatedTickCount64: null,
            highWaterMarkUtc: nowUtc,
            isServerSideEvaluation: true);

        if (!eval.IsDeviceBoundValid)
        {
            RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
                "UNAUTHORIZED_ATTEMPT", "BLOCKED",
                $"Device binding mismatch during validation: expected {lic.DeviceId}, got {incomingDeviceId}.",
                terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);
            await _context.SaveChangesAsync(ct);

            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = eval.BlockReasonCode,
                Message = eval.BlockMessage ?? "Device binding mismatch.",
                LicensePackage = ToSignedPackageDto(lic, terminal, nowUtc),
                Evaluation = eval,
                ServerUtcNow = nowUtc
            };
        }

        lic.LastValidatedAt = nowUtc;
        lic.UpdatedAt = nowUtc;
        RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, incomingDeviceId,
            "VALIDATED", eval.IsValidForNewSales ? "SUCCESS" : "BLOCKED",
            $"Online validation completed: EffectiveStatus={eval.EffectiveStatus}, RemainingDays={eval.RemainingDays ?? 0}.",
            terminal.TerminalCode, ipAddress, nowUtc, request.DeviceClockUtc);

        await _context.SaveChangesAsync(ct);

        return new LicenseOperationResponseDto
        {
            Success = eval.IsValidForNewSales,
            ErrorCode = eval.BlockReasonCode,
            Message = eval.IsValidForNewSales
                ? (eval.WarningMessage ?? $"License validated ({eval.RemainingDays} day(s) remaining).")
                : (eval.BlockMessage ?? $"License status is {eval.EffectiveStatus}."),
            LicensePackage = ToSignedPackageDto(lic, terminal, nowUtc),
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<LicenseOperationResponseDto> RenewLicenseAsync(
        RenewTerminalLicenseRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null)
        {
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "TERMINAL_NOT_FOUND", Message = "Terminal not found.", ServerUtcNow = nowUtc };
        }

        string? cleanKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        string reason = !string.IsNullOrWhiteSpace(request.RenewalReason)
            ? request.RenewalReason.Trim()
            : (!string.IsNullOrWhiteSpace(request.RenewalNotes) ? request.RenewalNotes.Trim() : string.Empty);

        if (reason.Length < 3)
        {
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "INVALID_REASON",
                Message = "A renewal reason of at least 3 characters is required.",
                ServerUtcNow = nowUtc
            };
        }

        if (!LicenseCryptoService.TryNormalizeDuration(
                request.RenewalDuration,
                request.RenewalUnit,
                request.AdditionalYears,
                out int renewVal,
                out string renewUnit,
                out string? durErr))
        {
            RecordAudit(null, terminal.TerminalId, terminal.BranchId, terminal.OutletId, null,
                "RENEW_REJECTED", "BLOCKED", durErr ?? "Invalid renewal duration.", actor, ipAddress, nowUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "INVALID_DURATION",
                Message = durErr ?? "Invalid renewal duration.",
                ServerUtcNow = nowUtc
            };
        }

        var lic = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
        if (lic == null)
        {
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "LICENSE_MISSING", Message = "No license exists for this terminal. Issue a license first.", ServerUtcNow = nowUtc };
        }

        // Idempotency guard: prevent duplicate extensions on retried or repeated submissions
        if (cleanKey != null)
        {
            var existingRenewal = await _context.LicenseRenewalHistories
                .AsNoTracking()
                .FirstOrDefaultAsync(h => h.TerminalId == terminal.TerminalId && h.IdempotencyKey == cleanKey, ct);
            if (existingRenewal != null)
            {
                if (existingRenewal.RenewalDuration != renewVal ||
                    !string.Equals(existingRenewal.RenewalUnit, renewUnit, StringComparison.OrdinalIgnoreCase))
                {
                    RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, lic.DeviceId,
                        "RENEW_IDEMPOTENCY_CONFLICT", "BLOCKED",
                        $"Conflicting renewal parameters supplied for existing IdempotencyKey '{cleanKey}'.",
                        actor, ipAddress, nowUtc);
                    await _context.SaveChangesAsync(ct);
                    return new LicenseOperationResponseDto
                    {
                        Success = false,
                        ErrorCode = "IDEMPOTENCY_CONFLICT",
                        Message = "Duplicate idempotency key with different renewal duration parameters.",
                        ServerUtcNow = nowUtc
                    };
                }

                var replayPkg = ToSignedPackageDto(lic, terminal, nowUtc);
                var replayEval = LicenseCryptoService.EvaluateLicense(
                    replayPkg.Payload, replayPkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
                    lic.DeviceId, lic.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);
                return new LicenseOperationResponseDto
                {
                    Success = true,
                    IsIdempotentReplay = true,
                    Message = $"Renewal already processed idempotently (Expires {lic.ExpiresAt:yyyy-MM-dd}, Version {lic.LicenseVersion}).",
                    LicensePackage = replayPkg,
                    Evaluation = replayEval,
                    ServerUtcNow = nowUtc
                };
            }
        }

        // Verify PostgreSQL license signature before trusting existing ExpiresAt baseline for renewal calculation
        if (!request.IsAdjustment && !LicenseCryptoService.VerifySignature(ToCanonicalPayload(lic), lic.Signature))
        {
            RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, lic.DeviceId,
                "TAMPER_DETECTED", "BLOCKED",
                "Renewal blocked: central database TerminalLicenses record failed digital signature verification.",
                actor, ipAddress, nowUtc);
            await _context.SaveChangesAsync(ct);
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "SIGNATURE_INVALID",
                Message = "Server license record failed signature verification before renewal. Perform an auditable administrative adjustment/re-issuance instead.",
                ServerUtcNow = nowUtc
            };
        }

        DateTime effectiveNow = request.EffectiveAtUtc.HasValue
            ? LicenseCryptoService.NormalizeUtcSecond(request.EffectiveAtUtc.Value)
            : nowUtc;

        DateTime? prevExpires = lic.ExpiresAt;
        int prevVersion = lic.LicenseVersion;

        bool isActiveUnexpired = lic.ExpiresAt.HasValue &&
                                 lic.ExpiresAt.Value > effectiveNow &&
                                 !string.Equals(lic.LicenseStatus, LicenseStatusConstants.Expired, StringComparison.OrdinalIgnoreCase);

        DateTime baseDate = (isActiveUnexpired && !request.IsAdjustment)
            ? lic.ExpiresAt!.Value
            : effectiveNow;

        DateTime newExpires = request.ExplicitNewExpiresAtUtc.HasValue
            ? LicenseCryptoService.NormalizeUtcSecond(request.ExplicitNewExpiresAtUtc.Value)
            : LicenseCryptoService.CalculateExpirationUtc(baseDate, renewVal, renewUnit);

        if (!request.IsAdjustment && newExpires <= effectiveNow)
        {
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "INVALID_EXPIRATION", Message = "Renewed expiration date must be in the future.", ServerUtcNow = nowUtc };
        }

        // Preserve original granted duration from initial issuance
        if (lic.OriginalDurationValue <= 0)
        {
            lic.OriginalDurationValue = lic.DurationValue > 0 ? lic.DurationValue : (lic.DurationYears > 0 ? lic.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears);
        }
        if (string.IsNullOrWhiteSpace(lic.OriginalDurationUnit))
        {
            lic.OriginalDurationUnit = string.IsNullOrWhiteSpace(lic.DurationUnit) ? LicenseDurationUnitConstants.Years : lic.DurationUnit;
        }

        lic.ActivatedAt ??= effectiveNow;
        lic.ExpiresAt = newExpires;
        lic.DurationValue = renewVal;
        lic.DurationUnit = renewUnit;
        lic.DurationYears = LicenseCryptoService.ApproximateDurationYears(renewVal, renewUnit);
        if (!request.IsAdjustment)
        {
            lic.RenewalCount++;
            lic.LastRenewedAt = nowUtc;
        }
        lic.LicenseStatus = string.IsNullOrWhiteSpace(lic.DeviceId)
            ? LicenseStatusConstants.PendingActivation
            : LicenseStatusConstants.Active;
        lic.RevokedAt = null;
        lic.RevocationReason = null;
        lic.SuspendedAt = null;
        lic.SuspensionReason = null;
        lic.LastValidatedAt = nowUtc;
        lic.LicenseVersion++;
        lic.UpdatedAt = nowUtc;
        SignLicenseEntity(lic);

        string renewalStatus = request.IsAdjustment
            ? LicenseRenewalStatusConstants.Adjustment
            : LicenseRenewalStatusConstants.Completed;
        string renewLabel = LicenseCryptoService.FormatDurationLabel(renewVal, renewUnit);

        _context.LicenseRenewalHistories.Add(new LicenseRenewalHistory
        {
            LicenseId = lic.LicenseId,
            TerminalId = terminal.TerminalId,
            PreviousExpiresAt = prevExpires,
            RenewalDuration = renewVal,
            RenewalUnit = renewUnit,
            NewExpiresAt = newExpires,
            PreviousLicenseVersion = prevVersion,
            NewLicenseVersion = lic.LicenseVersion,
            RenewedBy = actor,
            RenewalReason = reason,
            RenewalNotes = reason,
            RenewalStatus = renewalStatus,
            IdempotencyKey = cleanKey,
            RenewedAt = nowUtc
        });

        RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, lic.DeviceId,
            request.IsAdjustment ? "ADJUSTED" : "RENEWED", "SUCCESS",
            $"{(request.IsAdjustment ? "Adjusted" : "Renewed")} license {lic.LicenseKey} (+{renewLabel}) from {prevExpires:yyyy-MM-dd} to {newExpires:yyyy-MM-dd} (v{lic.LicenseVersion}). Reason: {reason}",
            actor, ipAddress, nowUtc);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (cleanKey != null)
        {
            _context.ChangeTracker.Clear();
            var existingOnRace = await _context.TerminalLicenses.AsNoTracking().FirstOrDefaultAsync(l => l.TerminalId == terminal.TerminalId, ct);
            if (existingOnRace != null)
            {
                var pkgRace = ToSignedPackageDto(existingOnRace, terminal, nowUtc);
                var evalRace = LicenseCryptoService.EvaluateLicense(
                    pkgRace.Payload, pkgRace.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
                    existingOnRace.DeviceId, existingOnRace.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);
                return new LicenseOperationResponseDto
                {
                    Success = true,
                    IsIdempotentReplay = true,
                    Message = $"Renewal already processed idempotently (Expires {existingOnRace.ExpiresAt:yyyy-MM-dd}, Version {existingOnRace.LicenseVersion}).",
                    LicensePackage = pkgRace,
                    Evaluation = evalRace,
                    ServerUtcNow = nowUtc
                };
            }
            throw;
        }

        var pkg = ToSignedPackageDto(lic, terminal, nowUtc);
        var eval = LicenseCryptoService.EvaluateLicense(
            pkg.Payload, pkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
            lic.DeviceId, lic.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);

        return new LicenseOperationResponseDto
        {
            Success = true,
            IsIdempotentReplay = false,
            Message = request.IsAdjustment
                ? $"License adjusted until {newExpires:yyyy-MM-dd} (Version {lic.LicenseVersion})."
                : $"License renewed (+{renewLabel}) until {newExpires:yyyy-MM-dd} (Version {lic.LicenseVersion}).",
            LicensePackage = pkg,
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<LicenseOperationResponseDto> SuspendLicenseAsync(
        ChangeLicenseStatusRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        return await UpdateAdministrativeStatusAsync(request.TerminalId, LicenseStatusConstants.Suspended, request.Reason, actor, ipAddress, ct);
    }

    public async Task<LicenseOperationResponseDto> ReactivateLicenseAsync(
        ChangeLicenseStatusRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        return await UpdateAdministrativeStatusAsync(request.TerminalId, LicenseStatusConstants.Active, request.Reason, actor, ipAddress, ct);
    }

    public async Task<LicenseOperationResponseDto> RevokeLicenseAsync(
        ChangeLicenseStatusRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        return await UpdateAdministrativeStatusAsync(request.TerminalId, LicenseStatusConstants.Revoked, request.Reason, actor, ipAddress, ct);
    }

    private async Task<LicenseOperationResponseDto> UpdateAdministrativeStatusAsync(
        int terminalId,
        string targetStatus,
        string reason,
        string actor,
        string? ipAddress,
        CancellationToken ct)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == terminalId, ct);

        if (terminal == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "TERMINAL_NOT_FOUND", Message = "Terminal not found.", ServerUtcNow = nowUtc };

        var lic = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == terminalId, ct);
        if (lic == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "LICENSE_MISSING", Message = "No license exists for this terminal.", ServerUtcNow = nowUtc };

        lic.LicenseStatus = targetStatus;
        lic.LicenseVersion++;
        lic.UpdatedAt = nowUtc;

        if (targetStatus == LicenseStatusConstants.Revoked)
        {
            lic.RevokedAt = nowUtc;
            lic.RevocationReason = reason?.Trim();
        }
        else if (targetStatus == LicenseStatusConstants.Suspended)
        {
            lic.SuspendedAt = nowUtc;
            lic.SuspensionReason = reason?.Trim();
        }
        else if (targetStatus == LicenseStatusConstants.Active)
        {
            lic.RevokedAt = null;
            lic.RevocationReason = null;
            lic.SuspendedAt = null;
            lic.SuspensionReason = null;
            lic.ActivatedAt ??= nowUtc;
            lic.ExpiresAt ??= LicenseCryptoService.CalculateExpirationUtc(
                lic.ActivatedAt.Value,
                lic.DurationValue > 0 ? lic.DurationValue : (lic.DurationYears > 0 ? lic.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears),
                lic.DurationUnit);
            lic.LastValidatedAt = nowUtc;
        }

        SignLicenseEntity(lic);

        RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, lic.DeviceId,
            targetStatus, "SUCCESS",
            $"License status changed to {targetStatus} (v{lic.LicenseVersion}) by {actor}. Reason: {reason}",
            actor, ipAddress, nowUtc);

        await _context.SaveChangesAsync(ct);

        var pkg = ToSignedPackageDto(lic, terminal, nowUtc);
        var eval = LicenseCryptoService.EvaluateLicense(
            pkg.Payload, pkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
            lic.DeviceId, lic.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);

        return new LicenseOperationResponseDto
        {
            Success = true,
            Message = $"Terminal license status updated to {targetStatus}.",
            LicensePackage = pkg,
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<LicenseOperationResponseDto> DeactivateDeviceAsync(
        ChangeLicenseStatusRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "TERMINAL_NOT_FOUND", Message = "Terminal not found.", ServerUtcNow = nowUtc };

        var lic = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == request.TerminalId, ct);
        if (lic == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "LICENSE_MISSING", Message = "No license exists for this terminal.", ServerUtcNow = nowUtc };

        string? oldDeviceId = lic.DeviceId;
        lic.DeviceId = string.Empty;
        lic.LicenseStatus = LicenseStatusConstants.PendingActivation;
        lic.LicenseVersion++;
        lic.UpdatedAt = nowUtc;
        SignLicenseEntity(lic);

        _context.LicenseDeviceHistories.Add(new LicenseDeviceHistory
        {
            LicenseId = lic.LicenseId,
            TerminalId = terminal.TerminalId,
            OldDeviceId = oldDeviceId,
            NewDeviceId = null,
            ActionType = "DEACTIVATION",
            AuthorizedBy = actor,
            Reason = request.Reason,
            OccurredAt = nowUtc
        });

        RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, oldDeviceId,
            "DEVICE_DEACTIVATED", "SUCCESS",
            $"Device {oldDeviceId} unbound from terminal {terminal.TerminalCode}. Reason: {request.Reason}",
            actor, ipAddress, nowUtc);

        await _context.SaveChangesAsync(ct);

        return new LicenseOperationResponseDto
        {
            Success = true,
            Message = "Device unbound from license. The terminal can now be activated on a replacement device.",
            LicensePackage = ToSignedPackageDto(lic, terminal, nowUtc),
            ServerUtcNow = nowUtc
        };
    }

    public async Task<LicenseOperationResponseDto> ReplaceDeviceAsync(
        ReplaceLicenseDeviceRequestDto request,
        string actor,
        string? ipAddress = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var terminal = await _context.Terminals
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, ct);

        if (terminal == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "TERMINAL_NOT_FOUND", Message = "Terminal not found.", ServerUtcNow = nowUtc };

        var lic = await _context.TerminalLicenses.FirstOrDefaultAsync(l => l.TerminalId == request.TerminalId, ct);
        if (lic == null)
            return new LicenseOperationResponseDto { Success = false, ErrorCode = "LICENSE_MISSING", Message = "No license exists for this terminal.", ServerUtcNow = nowUtc };

        string newDeviceId = !string.IsNullOrWhiteSpace(request.NewDeviceId)
            ? request.NewDeviceId.Trim().ToUpperInvariant()
            : (!string.IsNullOrWhiteSpace(request.NewDeviceUuid)
                ? DeviceBindingHelper.ComputeDeviceId(request.NewDeviceUuid.Trim())
                : string.Empty);

        if (!string.IsNullOrWhiteSpace(request.NewDeviceUuid))
        {
            terminal.Uuid = request.NewDeviceUuid.Trim();
            terminal.UpdatedAt = nowUtc;
        }

        string? oldDeviceId = lic.DeviceId;
        lic.DeviceId = newDeviceId;
        lic.LicenseStatus = string.IsNullOrWhiteSpace(newDeviceId)
            ? LicenseStatusConstants.PendingActivation
            : LicenseStatusConstants.Active;
        lic.ActivatedAt ??= nowUtc;
        lic.ExpiresAt ??= LicenseCryptoService.CalculateExpirationUtc(
            lic.ActivatedAt.Value,
            lic.DurationValue > 0 ? lic.DurationValue : (lic.DurationYears > 0 ? lic.DurationYears : LicenseCryptoService.DefaultLicenseDurationYears),
            lic.DurationUnit);
        lic.LastValidatedAt = nowUtc;
        lic.LicenseVersion++;
        lic.UpdatedAt = nowUtc;
        SignLicenseEntity(lic);

        _context.LicenseDeviceHistories.Add(new LicenseDeviceHistory
        {
            LicenseId = lic.LicenseId,
            TerminalId = terminal.TerminalId,
            OldDeviceId = oldDeviceId,
            NewDeviceId = string.IsNullOrWhiteSpace(newDeviceId) ? null : newDeviceId,
            ActionType = "DEVICE_REPLACEMENT",
            AuthorizedBy = actor,
            Reason = request.Reason,
            OccurredAt = nowUtc
        });

        RecordAudit(lic, terminal.TerminalId, terminal.BranchId, terminal.OutletId, newDeviceId,
            "DEVICE_REPLACED", "SUCCESS",
            $"Authorized device replacement for terminal {terminal.TerminalCode}: '{oldDeviceId}' -> '{newDeviceId}' (v{lic.LicenseVersion}). Reason: {request.Reason}",
            actor, ipAddress, nowUtc);

        await _context.SaveChangesAsync(ct);

        var pkg = ToSignedPackageDto(lic, terminal, nowUtc);
        var eval = LicenseCryptoService.EvaluateLicense(
            pkg.Payload, pkg.Signature, terminal.TerminalId, terminal.BranchId, terminal.OutletId,
            newDeviceId, lic.EnvironmentType, nowUtc, BusinessDayClock.TickCount64(), nowUtc, null, nowUtc, true);

        return new LicenseOperationResponseDto
        {
            Success = true,
            Message = string.IsNullOrWhiteSpace(newDeviceId)
                ? "Old device deactivated and replacement authorized. Activate the new device to bind it."
                : $"License rebound to replacement device {newDeviceId} (Version {lic.LicenseVersion}).",
            LicensePackage = pkg,
            Evaluation = eval,
            ServerUtcNow = nowUtc
        };
    }

    public async Task<(bool Allowed, string? ErrorMessage, LicenseEvaluationResultDto? Evaluation)> VerifyTerminalCanOperateAsync(
        int terminalId,
        string? deviceId = null,
        CancellationToken ct = default)
    {
        var nowUtc = LicenseCryptoService.NormalizeUtcSecond(BusinessDayClock.UtcNow());
        var lic = await _context.TerminalLicenses
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.TerminalId == terminalId, ct);

        if (lic == null)
        {
            // Controlled migration path: existing pre-licensing terminals without a row yet are allowed
            // until their license record is provisioned on activation/validation/admin issuance.
            return (true, null, null);
        }

        var payload = ToCanonicalPayload(lic);
        string targetDeviceId = !string.IsNullOrWhiteSpace(deviceId) ? deviceId : lic.DeviceId;

        var eval = LicenseCryptoService.EvaluateLicense(
            payload: payload,
            signatureBase64: lic.Signature,
            expectedTerminalId: lic.TerminalId,
            expectedBranchId: lic.BranchId,
            expectedOutletId: lic.OutletId,
            currentDeviceId: targetDeviceId,
            expectedEnvironment: lic.EnvironmentType,
            deviceUtcNow: nowUtc,
            currentTickCount64: BusinessDayClock.TickCount64(),
            lastValidatedServerUtc: lic.LastValidatedAt ?? nowUtc,
            lastValidatedTickCount64: null,
            highWaterMarkUtc: nowUtc,
            isServerSideEvaluation: true);

        if (!eval.IsValidForNewSales)
        {
            return (false, eval.BlockMessage ?? $"Terminal license status is {eval.EffectiveStatus}.", eval);
        }

        return (true, null, eval);
    }

    public async Task<List<LicenseRenewalHistoryDto>> GetRenewalHistoryAsync(int? terminalId = null, CancellationToken ct = default)
    {
        var query = _context.LicenseRenewalHistories.AsNoTracking().AsQueryable();
        if (terminalId.HasValue && terminalId.Value > 0)
            query = query.Where(h => h.TerminalId == terminalId.Value);

        var rows = await query.OrderByDescending(h => h.RenewedAt).Take(200).ToListAsync(ct);
        var termIds = rows.Select(r => r.TerminalId).Distinct().ToList();
        var termCodes = await _context.Terminals.AsNoTracking()
            .Where(t => termIds.Contains(t.TerminalId))
            .ToDictionaryAsync(t => t.TerminalId, t => t.TerminalCode, ct);

        return rows.Select(r => new LicenseRenewalHistoryDto
        {
            RenewalId = r.RenewalId,
            LicenseId = r.LicenseId,
            TerminalId = r.TerminalId,
            TerminalCode = termCodes.GetValueOrDefault(r.TerminalId, $"T-{r.TerminalId}"),
            PreviousExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(r.PreviousExpiresAt),
            RenewalDuration = r.RenewalDuration > 0 ? r.RenewalDuration : 1,
            RenewalUnit = string.IsNullOrWhiteSpace(r.RenewalUnit) ? LicenseDurationUnitConstants.Years : r.RenewalUnit,
            NewExpiresAtUtc = LicenseCryptoService.NormalizeUtcSecond(r.NewExpiresAt),
            PreviousLicenseVersion = r.PreviousLicenseVersion,
            NewLicenseVersion = r.NewLicenseVersion,
            RenewedBy = r.RenewedBy,
            RenewalReason = !string.IsNullOrWhiteSpace(r.RenewalReason) ? r.RenewalReason : r.RenewalNotes,
            RenewalNotes = r.RenewalNotes ?? r.RenewalReason,
            RenewalStatus = string.IsNullOrWhiteSpace(r.RenewalStatus) ? LicenseRenewalStatusConstants.Completed : r.RenewalStatus,
            IdempotencyKey = r.IdempotencyKey,
            RenewedAtUtc = LicenseCryptoService.NormalizeUtcSecond(r.RenewedAt)
        }).ToList();
    }

    public async Task<List<LicenseDeviceHistoryDto>> GetDeviceHistoryAsync(int? terminalId = null, CancellationToken ct = default)
    {
        var query = _context.LicenseDeviceHistories.AsNoTracking().AsQueryable();
        if (terminalId.HasValue && terminalId.Value > 0)
            query = query.Where(h => h.TerminalId == terminalId.Value);

        var rows = await query.OrderByDescending(h => h.OccurredAt).Take(200).ToListAsync(ct);
        var termIds = rows.Select(r => r.TerminalId).Distinct().ToList();
        var termCodes = await _context.Terminals.AsNoTracking()
            .Where(t => termIds.Contains(t.TerminalId))
            .ToDictionaryAsync(t => t.TerminalId, t => t.TerminalCode, ct);

        return rows.Select(r => new LicenseDeviceHistoryDto
        {
            DeviceHistoryId = r.DeviceHistoryId,
            LicenseId = r.LicenseId,
            TerminalId = r.TerminalId,
            TerminalCode = termCodes.GetValueOrDefault(r.TerminalId, $"T-{r.TerminalId}"),
            OldDeviceId = r.OldDeviceId,
            NewDeviceId = r.NewDeviceId,
            ActionType = r.ActionType,
            AuthorizedBy = r.AuthorizedBy,
            Reason = r.Reason,
            OccurredAtUtc = LicenseCryptoService.NormalizeUtcSecond(r.OccurredAt)
        }).ToList();
    }

    public async Task<List<LicenseAuditLogDto>> GetAuditLogsAsync(int? terminalId = null, int limit = 100, CancellationToken ct = default)
    {
        var query = _context.LicenseAuditLogs.AsNoTracking().AsQueryable();
        if (terminalId.HasValue && terminalId.Value > 0)
            query = query.Where(a => a.TerminalId == terminalId.Value);

        var rows = await query.OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.AuditLogId)
            .Take(Math.Clamp(limit, 1, 500))
            .ToListAsync(ct);

        var termIds = rows.Where(r => r.TerminalId.HasValue).Select(r => r.TerminalId!.Value).Distinct().ToList();
        var termCodes = await _context.Terminals.AsNoTracking()
            .Where(t => termIds.Contains(t.TerminalId))
            .ToDictionaryAsync(t => t.TerminalId, t => t.TerminalCode, ct);

        return rows.Select(r => new LicenseAuditLogDto
        {
            AuditLogId = r.AuditLogId,
            LicenseId = r.LicenseId,
            TerminalId = r.TerminalId,
            TerminalCode = r.TerminalId.HasValue ? termCodes.GetValueOrDefault(r.TerminalId.Value, $"T-{r.TerminalId.Value}") : null,
            BranchId = r.BranchId,
            OutletId = r.OutletId,
            DeviceId = r.DeviceId,
            EventType = r.EventType,
            EventStatus = r.EventStatus,
            Message = r.Message,
            Actor = r.Actor,
            IpAddress = r.IpAddress,
            DeviceClockUtc = LicenseCryptoService.NormalizeUtcSecond(r.DeviceClockUtc),
            ServerClockUtc = LicenseCryptoService.NormalizeUtcSecond(r.ServerClockUtc),
            ClockSkewSeconds = r.ClockSkewSeconds,
            OccurredAtUtc = LicenseCryptoService.NormalizeUtcSecond(r.OccurredAt)
        }).ToList();
    }

    public async Task<List<ClientLicenseSummaryDto>> GetClientLicenseSummaryAsync(int organizationId, CancellationToken ct = default)
    {
        var terminals = await _context.Terminals
            .AsNoTracking()
            .Include(t => t.Branch)
            .Include(t => t.Outlet)
            .Where(t => t.Branch != null && t.Branch.OrganizationId == organizationId)
            .OrderBy(t => t.TerminalCode)
            .ToListAsync(ct);

        var terminalIds = terminals.Select(t => t.TerminalId).ToList();
        var licenses = await _context.TerminalLicenses
            .AsNoTracking()
            .Where(l => terminalIds.Contains(l.TerminalId))
            .ToDictionaryAsync(l => l.TerminalId, ct);

        var nowUtc = DateTime.UtcNow;
        return terminals.Select(t =>
        {
            licenses.TryGetValue(t.TerminalId, out var lic);
            var validUntil = lic != null ? LicenseCryptoService.NormalizeUtcSecond(lic.ExpiresAt) : (DateTime?)null;
            int daysRemaining = validUntil.HasValue ? Math.Max(0, (int)Math.Ceiling((validUntil.Value - nowUtc).TotalDays)) : 0;
            return new ClientLicenseSummaryDto
            {
                TerminalId = t.TerminalId,
                TerminalCode = t.TerminalCode,
                TerminalName = t.Name,
                BranchName = t.Branch?.Name ?? string.Empty,
                OutletName = t.Outlet?.Name ?? string.Empty,
                Status = lic?.LicenseStatus ?? "UNLICENSED",
                ValidFromUtc = lic != null ? LicenseCryptoService.NormalizeUtcSecond(lic.IssuedAt) : null,
                ValidUntilUtc = validUntil,
                DaysRemaining = daysRemaining
            };
        }).ToList();
    }
}
