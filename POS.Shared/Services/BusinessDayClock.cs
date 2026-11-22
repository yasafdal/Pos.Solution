using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using POS.Shared.Models;

namespace POS.Shared.Services;

/// <summary>
/// Authoritative Asia/Manila (UTC+08) 4:00 AM business-day clock and anti-clock-tamper lock evaluator.
/// Business day D starts at D 04:00:00 Asia/Manila (D-1 20:00:00 UTC) and ends at D+1 04:00:00 Asia/Manila (D 20:00:00 UTC).
/// </summary>
public static class BusinessDayClock
{
    public const string DefaultTimeZoneId = "Asia/Manila";
    public const string WindowsManilaTimeZoneId = "Singapore Standard Time";
    public const int SuspiciousClockSkewThresholdSeconds = 300; // 5 minutes
    public static readonly TimeSpan BusinessDayCutoffTimeOfDay = TimeSpan.FromHours(4); // 04:00 AM Philippine Time

    public static TimeZoneInfo ManilaTimeZone { get; } = ResolveManilaTimeZone();

    private static Func<DateTime> _utcNowProvider = static () => DateTime.UtcNow;
    private static Func<long> _tickCount64Provider = static () => Environment.TickCount64;
    private static Func<DateTimeOffset>? _deviceNowOffsetProvider;
    private static Func<TimeZoneInfo>? _deviceTimeZoneProvider;

    public static Func<DateTime> UtcNowProvider
    {
        get => _utcNowProvider;
        set => _utcNowProvider = value ?? (static () => DateTime.UtcNow);
    }

    public static Func<long> TickCount64Provider
    {
        get => _tickCount64Provider;
        set => _tickCount64Provider = value ?? (static () => Environment.TickCount64);
    }

    public static Func<DateTimeOffset>? DeviceNowOffsetProvider
    {
        get => _deviceNowOffsetProvider;
        set => _deviceNowOffsetProvider = value;
    }

    public static Func<TimeZoneInfo>? DeviceTimeZoneProvider
    {
        get => _deviceTimeZoneProvider;
        set => _deviceTimeZoneProvider = value;
    }

    public static void ResetProviders()
    {
        _utcNowProvider = static () => DateTime.UtcNow;
        _tickCount64Provider = static () => Environment.TickCount64;
        _deviceNowOffsetProvider = null;
        _deviceTimeZoneProvider = null;
    }

    private static TimeZoneInfo ResolveManilaTimeZone()
    {
        foreach (var candidateId in new[] { DefaultTimeZoneId, WindowsManilaTimeZoneId })
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(candidateId);
                if (tz.BaseUtcOffset == TimeSpan.FromHours(8))
                    return tz;
            }
            catch
            {
                // Continue to fallback candidate
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            DefaultTimeZoneId,
            TimeSpan.FromHours(8),
            "Philippine Standard Time (Asia/Manila)",
            "Philippine Standard Time");
    }

    public static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return ManilaTimeZone;

        var trimmed = timeZoneId.Trim();
        if (trimmed.Equals(DefaultTimeZoneId, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals(WindowsManilaTimeZoneId, StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Philippine Standard Time", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("PHT", StringComparison.OrdinalIgnoreCase))
        {
            return ManilaTimeZone;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(trimmed);
        }
        catch
        {
            return ManilaTimeZone;
        }
    }

    public static DateTime UtcNow() => DateTime.SpecifyKind(_utcNowProvider(), DateTimeKind.Utc);

    /// <summary>
    /// Returns the authoritative Asia/Manila (UTC+08) wall-clock date and time as DateTimeKind.Unspecified.
    /// Derived from UtcNow() so tests using UtcNowProvider remain fully deterministic.
    /// </summary>
    public static DateTime ManilaNow() =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(UtcNow(), ManilaTimeZone), DateTimeKind.Unspecified);

    /// <summary>
    /// Converts a Manila wall-clock timestamp (DateTimeKind.Unspecified) to canonical UTC (DateTimeKind.Utc).
    /// Explicit DateTimeKind.Utc values are returned unchanged.
    /// </summary>
    public static DateTime ManilaToUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), ManilaTimeZone), DateTimeKind.Utc)
        };

    public static long TickCount64() => _tickCount64Provider();

    public static TimeZoneInfo GetDeviceTimeZone() =>
        _deviceTimeZoneProvider?.Invoke() ?? TimeZoneInfo.Local;

    public static string GetDeviceTimeZoneId()
    {
        var tz = GetDeviceTimeZone();
        return string.IsNullOrWhiteSpace(tz.Id) ? DefaultTimeZoneId : tz.Id;
    }

    public static DateTimeOffset DeviceNowOffset()
    {
        if (_deviceNowOffsetProvider != null)
            return _deviceNowOffsetProvider();

        var utc = UtcNow();
        var tz = GetDeviceTimeZone();
        var offset = tz.GetUtcOffset(utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tz);
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
    }

    public static int GetDeviceUtcOffsetMinutes() =>
        (int)Math.Round(DeviceNowOffset().Offset.TotalMinutes);

    /// <summary>
    /// Returns the POS device's current local wall-clock date and time as DateTimeKind.Unspecified
    /// so it is stored, serialized, and displayed verbatim without UTC offset conversion.
    /// </summary>
    public static DateTime DeviceNow() =>
        DateTime.SpecifyKind(DeviceNowOffset().DateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// Preserves device-local wall-clock timestamps (DateTimeKind.Unspecified or DateTimeKind.Local) verbatim.
    /// Only converts timestamps that are explicitly tagged as DateTimeKind.Utc.
    /// </summary>
    public static DateTime NormalizeLocal(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(value, ManilaTimeZone), DateTimeKind.Unspecified)
            : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);

    public static DateTime NormalizeLocal(DateTimeOffset value) =>
        DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// Normalizes a DateTime to canonical UTC without double-converting values materialized from SQLite/PostgreSQL as Unspecified.
    /// </summary>
    public static DateTime NormalizeUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    public static DateTime NormalizeUtc(DateTimeOffset value) =>
        value.UtcDateTime;

    public static DateTimeOffset ToManilaOffset(DateTime value)
    {
        var local = NormalizeLocal(value);
        var offset = ManilaTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    public static DateTimeOffset ToManilaOffset(DateTimeOffset timestamp) =>
        timestamp;

    public static DateTime ToManilaTime(DateTime value) =>
        NormalizeLocal(value);

    public static DateTime ToManilaTime(DateTimeOffset timestamp) =>
        DateTime.SpecifyKind(timestamp.DateTime, DateTimeKind.Unspecified);

    public static DateTime ToBranchTime(DateTime value, string? branchTimeZoneId = null)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            var tz = ResolveTimeZone(branchTimeZoneId);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(value, tz), DateTimeKind.Unspecified);
        }

        return DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    public static DateTime ToBranchTime(DateTimeOffset timestamp, string? branchTimeZoneId = null) =>
        DateTime.SpecifyKind(timestamp.DateTime, DateTimeKind.Unspecified);

    /// <summary>
    /// Applies the 4:00 AM business-day cutoff to a local transaction timestamp (or converts an explicit UTC timestamp first).
    /// Transactions before 04:00 AM belong to the previous business date; transactions at or after 04:00 AM belong to that calendar date.
    /// </summary>
    public static DateOnly GetBusinessDate(DateTime value) =>
        GetBusinessDate(value, null);

    public static DateOnly GetBusinessDate(DateTime value, string? branchTimeZoneId)
    {
        var branchLocal = ToBranchTime(value, branchTimeZoneId);
        var businessDateLocal = branchLocal.TimeOfDay < BusinessDayCutoffTimeOfDay
            ? branchLocal.Date.AddDays(-1)
            : branchLocal.Date;
        return DateOnly.FromDateTime(businessDateLocal);
    }

    public static DateOnly GetBusinessDate(DateTimeOffset timestamp, string? branchTimeZoneId = null) =>
        GetBusinessDate(DateTime.SpecifyKind(timestamp.DateTime, DateTimeKind.Unspecified), branchTimeZoneId);

    public static DateOnly GetCurrentBusinessDate(string? branchTimeZoneId = null) =>
        _deviceNowOffsetProvider != null
            ? GetBusinessDate(DeviceNowOffset(), branchTimeZoneId)
            : GetBusinessDate(UtcNow(), branchTimeZoneId);

    public static string GetBusinessDateString(DateTime value) =>
        GetBusinessDate(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly GetShiftBusinessDate(DateTime shiftOpenedAt, string? recordedBusinessDate = null)
    {
        if (!string.IsNullOrWhiteSpace(recordedBusinessDate) &&
            DateOnly.TryParseExact(recordedBusinessDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }
        return GetBusinessDate(shiftOpenedAt);
    }

    public static string GetShiftBusinessDateString(DateTime shiftOpenedAt, string? recordedBusinessDate = null) =>
        GetShiftBusinessDate(shiftOpenedAt, recordedBusinessDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsPreviousBusinessDateShift(DateTime shiftOpenedAt, DateTime referenceTime, string? recordedBusinessDate = null)
    {
        var shiftDate = GetShiftBusinessDate(shiftOpenedAt, recordedBusinessDate);
        var currentDate = GetBusinessDate(referenceTime);
        return shiftDate < currentDate;
    }

    public static (DateTime StartLocal, DateTime EndLocal) GetPeriodLocal(DateOnly businessDate) =>
        (DateTime.SpecifyKind(businessDate.ToDateTime(new TimeOnly(4, 0)), DateTimeKind.Unspecified),
         DateTime.SpecifyKind(businessDate.AddDays(1).ToDateTime(new TimeOnly(4, 0)), DateTimeKind.Unspecified));

    public static DateTime GetCurrentBusinessDayStartLocal(string? branchTimeZoneId = null) =>
        GetPeriodLocal(GetCurrentBusinessDate(branchTimeZoneId)).StartLocal;

    public static (DateTime StartUtc, DateTime EndUtc) GetPeriodUtc(DateOnly businessDate) =>
        GetPeriodUtc(businessDate, null);

    public static (DateTime StartUtc, DateTime EndUtc) GetPeriodUtc(DateOnly businessDate, string? branchTimeZoneId)
    {
        var tz = ResolveTimeZone(branchTimeZoneId);
        var startLocal = DateTime.SpecifyKind(businessDate.ToDateTime(new TimeOnly(4, 0)), DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(businessDate.AddDays(1).ToDateTime(new TimeOnly(4, 0)), DateTimeKind.Unspecified);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startLocal, tz);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endLocal, tz);
        return (DateTime.SpecifyKind(startUtc, DateTimeKind.Utc), DateTime.SpecifyKind(endUtc, DateTimeKind.Utc));
    }

    public static DateTime GetCurrentBusinessDayStartUtc(string? branchTimeZoneId = null) =>
        GetPeriodUtc(GetCurrentBusinessDate(branchTimeZoneId), branchTimeZoneId).StartUtc;

    public static DateTime GetNextBusinessDayStartUtc(DateOnly businessDate) =>
        GetPeriodUtc(businessDate).EndUtc;

    public static DateTime GetNextBusinessDayStartUtc(DateTime closedAtUtc) =>
        GetNextBusinessDayStartUtc(GetBusinessDate(closedAtUtc));

    public static string FormatManila(DateTime value, string format = "yyyy-MM-dd HH:mm") =>
        ToManilaTime(value).ToString(format, CultureInfo.InvariantCulture);

    public static string FormatManila(DateTime? value, string format = "yyyy-MM-dd HH:mm", string fallback = "N/A") =>
        value.HasValue ? ToManilaTime(value.Value).ToString(format, CultureInfo.InvariantCulture) : fallback;

    public static string FormatManilaDateTime(DateTime value) =>
        ToManilaTime(value).ToString("yyyy-MM-dd hh:mm:ss tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";

    /// <summary>
    /// Compares device transaction/transmission timestamps against authoritative server receipt time
    /// to detect significant device clock skew without overwriting the original local occurrence timestamp.
    /// </summary>
    public static (int ClockSkewSeconds, bool IsSuspicious, string? WarningMessage) EvaluateClockDiscrepancy(
        DateTime transactionOccurredAt,
        DateTime serverReceivedAtUtc,
        DateTime? clientSentAtUtc = null,
        DateTime? shiftOpenedAt = null,
        int? deviceUtcOffsetMinutes = null)
    {
        int offsetMins = deviceUtcOffsetMinutes ?? GetDeviceUtcOffsetMinutes();
        var occurredUtc = transactionOccurredAt.Kind == DateTimeKind.Utc
            ? transactionOccurredAt
            : DateTime.SpecifyKind(transactionOccurredAt.AddMinutes(-offsetMins), DateTimeKind.Utc);
        var receivedUtc = NormalizeUtc(serverReceivedAtUtc);

        DateTime? shiftOpenedUtc = null;
        if (shiftOpenedAt.HasValue && shiftOpenedAt.Value != default)
        {
            if (shiftOpenedAt.Value.Kind == DateTimeKind.Utc)
            {
                shiftOpenedUtc = shiftOpenedAt.Value;
            }
            else
            {
                var asLocalDiff = Math.Abs((shiftOpenedAt.Value - transactionOccurredAt).TotalHours);
                var asUtcDiff = Math.Abs((shiftOpenedAt.Value - receivedUtc).TotalHours);
                shiftOpenedUtc = asLocalDiff < asUtcDiff
                    ? DateTime.SpecifyKind(shiftOpenedAt.Value.AddMinutes(-offsetMins), DateTimeKind.Utc)
                    : DateTime.SpecifyKind(shiftOpenedAt.Value, DateTimeKind.Utc);
            }
        }

        int skewSeconds = 0;
        if (clientSentAtUtc.HasValue && clientSentAtUtc.Value != default)
        {
            var sentUtc = NormalizeUtc(clientSentAtUtc.Value);
            skewSeconds = (int)Math.Round((sentUtc - receivedUtc).TotalSeconds);
        }
        else if (occurredUtc > receivedUtc)
        {
            // Device transaction timestamp is in the future relative to server receipt time
            skewSeconds = (int)Math.Round((occurredUtc - receivedUtc).TotalSeconds);
        }
        else if (shiftOpenedUtc.HasValue && occurredUtc < shiftOpenedUtc.Value)
        {
            // Device transaction timestamp precedes the shift opening timestamp
            skewSeconds = (int)Math.Round((occurredUtc - shiftOpenedUtc.Value).TotalSeconds);
        }

        if (Math.Abs(skewSeconds) < SuspiciousClockSkewThresholdSeconds && occurredUtc > receivedUtc.AddSeconds(SuspiciousClockSkewThresholdSeconds))
        {
            skewSeconds = (int)Math.Round((occurredUtc - receivedUtc).TotalSeconds);
        }

        bool isSuspicious = Math.Abs(skewSeconds) >= SuspiciousClockSkewThresholdSeconds;
        string? warning = isSuspicious
            ? $"[CLOCK_SKEW_DETECTED] Device clock discrepancy of {skewSeconds}s ({skewSeconds / 60.0:F1} min) detected. Original local occurrence timestamp ({transactionOccurredAt:yyyy-MM-dd HH:mm:ss}) preserved; ServerReceivedAtUtc=({receivedUtc:O})."
            : null;

        return (skewSeconds, isSuspicious, warning);
    }

    public static TerminalClosingLockDto EvaluateLock(
        int terminalId,
        string? closedBusinessDate,
        string? lockedByCashier,
        DateTime? lockedAtUtc,
        DateTime? unlockAtUtc,
        string? candidateCashierUsername = null,
        DateTime? authoritativeServerUtc = null,
        DateTime? deviceUtcNow = null,
        long? currentTickCount64 = null,
        long? lockedAtTickCount64 = null,
        DateTime? lastKnownServerUtc = null,
        long? lastKnownServerTickCount64 = null,
        IEnumerable<string>? additionalClosedCashiersToday = null)
    {
        DateTime currentDeviceUtc = DateTime.SpecifyKind(deviceUtcNow ?? UtcNow(), DateTimeKind.Utc);
        long currentTick64 = currentTickCount64 ?? TickCount64();

        DateOnly? parsedClosedDate = null;
        if (!string.IsNullOrWhiteSpace(closedBusinessDate) &&
            DateOnly.TryParseExact(closedBusinessDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
        {
            parsedClosedDate = parsedDate;
        }
        else if (lockedAtUtc.HasValue)
        {
            parsedClosedDate = GetBusinessDate(lockedAtUtc.Value);
        }

        var restrictedCashiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (additionalClosedCashiersToday != null)
        {
            foreach (var c in additionalClosedCashiersToday)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    restrictedCashiers.Add(c.Trim());
            }
        }

        if (!parsedClosedDate.HasValue)
        {
            DateTime effectiveNowNoTermLock = authoritativeServerUtc.HasValue
                ? DateTime.SpecifyKind(authoritativeServerUtc.Value, DateTimeKind.Utc)
                : currentDeviceUtc;
            string activeBizDate = GetBusinessDateString(effectiveNowNoTermLock);
            DateTime activeUnlockUtc = GetNextBusinessDayStartUtc(effectiveNowNoTermLock);
            bool cashierBlockedOnly = !string.IsNullOrWhiteSpace(candidateCashierUsername)
                && restrictedCashiers.Contains(candidateCashierUsername.Trim());
            string unlockFmtOnly = ToManilaTime(activeUnlockUtc).ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";

            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                IsTerminalLocked = false,
                IsCashierRestricted = cashierBlockedOnly,
                CurrentBusinessDate = activeBizDate,
                RestrictedCashiers = restrictedCashiers.ToList(),
                UnlockAtUtc = restrictedCashiers.Count > 0 ? activeUnlockUtc : null,
                ServerUtcNow = authoritativeServerUtc,
                CashierRestrictionMessage = cashierBlockedOnly
                    ? $"Cashier '{candidateCashierUsername!.Trim()}' has already completed Close Cashier on this terminal for business date {activeBizDate} and cannot log in or open another session on this terminal until {unlockFmtOnly}."
                    : null
            };
        }

        DateTime effectiveUnlockUtc = unlockAtUtc.HasValue
            ? DateTime.SpecifyKind(unlockAtUtc.Value, DateTimeKind.Utc)
            : GetNextBusinessDayStartUtc(parsedClosedDate.Value);

        DateTime? normalizedLockedAtUtc = lockedAtUtc.HasValue
            ? DateTime.SpecifyKind(lockedAtUtc.Value, DateTimeKind.Utc)
            : null;

        bool isLocked = false;
        bool clockTamperDetected = false;
        DateTime effectiveNowUtc;

        if (authoritativeServerUtc.HasValue)
        {
            effectiveNowUtc = DateTime.SpecifyKind(authoritativeServerUtc.Value, DateTimeKind.Utc);
            isLocked = effectiveNowUtc < effectiveUnlockUtc;
        }
        else
        {
            effectiveNowUtc = currentDeviceUtc;
            if (currentDeviceUtc < effectiveUnlockUtc)
            {
                isLocked = true;
            }
            else
            {
                // Device clock claims it is >= 4:00 AM next business day while offline.
                // Guard 1: Check for backward clock rollback below closing time or last server timestamp.
                if (normalizedLockedAtUtc.HasValue && currentDeviceUtc < normalizedLockedAtUtc.Value)
                {
                    isLocked = true;
                    clockTamperDetected = true;
                }
                else if (lastKnownServerUtc.HasValue && currentDeviceUtc < DateTime.SpecifyKind(lastKnownServerUtc.Value, DateTimeKind.Utc).AddMinutes(-2))
                {
                    isLocked = true;
                    clockTamperDetected = true;
                }

                // Guard 2: OS Monotonic uptime verification from closing anchor (catches forward OS clock jumps).
                if (!isLocked && normalizedLockedAtUtc.HasValue && lockedAtTickCount64.HasValue && lockedAtTickCount64.Value > 0 && currentTick64 >= lockedAtTickCount64.Value)
                {
                    long elapsedMs = currentTick64 - lockedAtTickCount64.Value;
                    DateTime maxTrustedUtcFromClose = normalizedLockedAtUtc.Value.AddMilliseconds(elapsedMs).AddMinutes(2);
                    if (maxTrustedUtcFromClose < effectiveUnlockUtc)
                    {
                        isLocked = true;
                        clockTamperDetected = true;
                        effectiveNowUtc = normalizedLockedAtUtc.Value.AddMilliseconds(elapsedMs);
                    }
                }

                // Guard 3: OS Monotonic uptime verification from last server sync anchor.
                if (!isLocked && lastKnownServerUtc.HasValue && lastKnownServerTickCount64.HasValue && lastKnownServerTickCount64.Value > 0 && currentTick64 >= lastKnownServerTickCount64.Value)
                {
                    long elapsedMs = currentTick64 - lastKnownServerTickCount64.Value;
                    DateTime serverAnchorUtc = DateTime.SpecifyKind(lastKnownServerUtc.Value, DateTimeKind.Utc);
                    DateTime maxTrustedUtcFromServer = serverAnchorUtc.AddMilliseconds(elapsedMs).AddMinutes(2);
                    if (maxTrustedUtcFromServer < effectiveUnlockUtc)
                    {
                        isLocked = true;
                        clockTamperDetected = true;
                        effectiveNowUtc = serverAnchorUtc.AddMilliseconds(elapsedMs);
                    }
                }
            }
        }

        string closedDateCanonical = parsedClosedDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string effectiveBusinessDate = GetBusinessDateString(effectiveNowUtc);
        bool isCandidateRestricted = !string.IsNullOrWhiteSpace(candidateCashierUsername)
            && restrictedCashiers.Contains(candidateCashierUsername.Trim());

        string unlockManilaFormatted = ToManilaTime(effectiveUnlockUtc).ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";
        string? lockMessage = null;
        string? cashierMessage = null;

        if (isLocked)
        {
            lockMessage = clockTamperDetected
                ? $"POS Terminal is finalized (Z-Reading) and locked for business day {closedDateCanonical}. Unverified device clock adjustment detected; terminal remains locked until verified 4:00 AM ({unlockManilaFormatted})."
                : $"Z-Reading has been finalized for this POS Terminal for business day {closedDateCanonical}. No cashier login, new sessions, or sales are permitted until {unlockManilaFormatted}.";
        }

        if (isCandidateRestricted)
        {
            string cDate = isLocked ? closedDateCanonical : effectiveBusinessDate;
            DateTime cUnlock = isLocked ? effectiveUnlockUtc : GetNextBusinessDayStartUtc(effectiveNowUtc);
            string cUnlockFmt = ToManilaTime(cUnlock).ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";
            cashierMessage = $"Cashier '{candidateCashierUsername!.Trim()}' has already completed Close Cashier on this terminal for business date {cDate} and cannot log in or open another session on this terminal until {cUnlockFmt}.";
        }

        return new TerminalClosingLockDto
        {
            TerminalId = terminalId,
            IsTerminalLocked = isLocked,
            IsCashierRestricted = isCandidateRestricted,
            ClockTamperDetected = clockTamperDetected,
            ClosedBusinessDate = isLocked ? closedDateCanonical : null,
            CurrentBusinessDate = effectiveBusinessDate,
            LockedByCashier = isLocked ? lockedByCashier : null,
            RestrictedCashiers = restrictedCashiers.ToList(),
            LockedAtUtc = isLocked ? normalizedLockedAtUtc : null,
            UnlockAtUtc = isLocked ? effectiveUnlockUtc : (restrictedCashiers.Count > 0 ? GetNextBusinessDayStartUtc(effectiveNowUtc) : null),
            ServerUtcNow = authoritativeServerUtc,
            LockMessage = lockMessage,
            CashierRestrictionMessage = cashierMessage
        };
    }
}
