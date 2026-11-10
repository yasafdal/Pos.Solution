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
    private static Func<DateTime> _utcNowProvider = static () => DateTime.UtcNow;
    private static Func<long> _tickCount64Provider = static () => Environment.TickCount64;

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

    public static void ResetProviders()
    {
        _utcNowProvider = static () => DateTime.UtcNow;
        _tickCount64Provider = static () => Environment.TickCount64;
    }

    public static DateTime UtcNow() => DateTime.SpecifyKind(_utcNowProvider(), DateTimeKind.Utc);

    public static long TickCount64() => _tickCount64Provider();

    /// <summary>
    /// Converts a UTC timestamp to its Asia/Manila (UTC+08) 4:00 AM cutoff business date.
    /// Subtracting 4 hours from Manila time (UTC+8 - 4h = UTC+4) maps [04:00 Manila, next day 03:59:59.999 Manila] to the same calendar date.
    /// </summary>
    public static DateOnly GetBusinessDate(DateTime utc) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4));

    public static string GetBusinessDateString(DateTime utc) =>
        GetBusinessDate(utc).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static (DateTime StartUtc, DateTime EndUtc) GetPeriodUtc(DateOnly businessDate)
    {
        var startUtc = DateTime.SpecifyKind(businessDate.ToDateTime(new TimeOnly(4, 0)).AddHours(-8), DateTimeKind.Utc);
        return (startUtc, startUtc.AddDays(1));
    }

    public static DateTime GetNextBusinessDayStartUtc(DateOnly businessDate) =>
        GetPeriodUtc(businessDate).EndUtc;

    public static DateTime GetNextBusinessDayStartUtc(DateTime closedAtUtc) =>
        GetNextBusinessDayStartUtc(GetBusinessDate(closedAtUtc));

    public static DateTime ToManilaTime(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(8);

    public static string FormatManilaDateTime(DateTime utc) =>
        ToManilaTime(utc).ToString("yyyy-MM-dd hh:mm:ss tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";

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

        if (!parsedClosedDate.HasValue)
        {
            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                IsTerminalLocked = false,
                IsCashierRestricted = false,
                CurrentBusinessDate = GetBusinessDateString(authoritativeServerUtc ?? currentDeviceUtc),
                ServerUtcNow = authoritativeServerUtc
            };
        }

        DateTime effectiveUnlockUtc = unlockAtUtc.HasValue
            ? DateTime.SpecifyKind(unlockAtUtc.Value, DateTimeKind.Utc)
            : GetNextBusinessDayStartUtc(parsedClosedDate.Value);

        DateTime? normalizedLockedAtUtc = lockedAtUtc.HasValue
            ? DateTime.SpecifyKind(lockedAtUtc.Value, DateTimeKind.Utc)
            : null;

        var restrictedCashiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(lockedByCashier))
            restrictedCashiers.Add(lockedByCashier.Trim());
        if (additionalClosedCashiersToday != null)
        {
            foreach (var c in additionalClosedCashiersToday)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    restrictedCashiers.Add(c.Trim());
            }
        }

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
        bool isCandidateRestricted = isLocked
            && !string.IsNullOrWhiteSpace(candidateCashierUsername)
            && restrictedCashiers.Contains(candidateCashierUsername.Trim());

        string unlockManilaFormatted = ToManilaTime(effectiveUnlockUtc).ToString("yyyy-MM-dd hh:mm tt", CultureInfo.InvariantCulture) + " (Asia/Manila)";
        string? lockMessage = null;
        string? cashierMessage = null;

        if (isLocked)
        {
            lockMessage = clockTamperDetected
                ? $"POS Terminal is closed and locked for business day {closedDateCanonical}. Unverified device clock adjustment detected; terminal remains locked until verified 4:00 AM ({unlockManilaFormatted})."
                : $"This POS Terminal is closed and locked for business day {closedDateCanonical}. No cashier login or transactions are permitted until {unlockManilaFormatted}.";

            if (isCandidateRestricted)
            {
                cashierMessage = $"Cashier '{candidateCashierUsername}' completed Cashier Closing on this Terminal for business day {closedDateCanonical} and is restricted from logging in or starting another session until {unlockManilaFormatted}.";
            }
        }

        return new TerminalClosingLockDto
        {
            TerminalId = terminalId,
            IsTerminalLocked = isLocked,
            IsCashierRestricted = isCandidateRestricted,
            ClockTamperDetected = clockTamperDetected,
            ClosedBusinessDate = isLocked ? closedDateCanonical : null,
            CurrentBusinessDate = GetBusinessDateString(effectiveNowUtc),
            LockedByCashier = isLocked ? lockedByCashier : null,
            RestrictedCashiers = isLocked ? restrictedCashiers.ToList() : new List<string>(),
            LockedAtUtc = isLocked ? normalizedLockedAtUtc : null,
            UnlockAtUtc = isLocked ? effectiveUnlockUtc : null,
            ServerUtcNow = authoritativeServerUtc,
            LockMessage = lockMessage,
            CashierRestrictionMessage = cashierMessage
        };
    }
}
