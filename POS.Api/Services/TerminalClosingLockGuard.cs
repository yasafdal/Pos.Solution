using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Api.Services;

public static class TerminalClosingLockGuard
{
    public static async Task<TerminalClosingLockDto> EvaluateTerminalLockAsync(
        PosDbContext db,
        int terminalId,
        string? cashierUsername = null,
        CancellationToken ct = default)
    {
        if (terminalId <= 0)
        {
            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                ServerUtcNow = BusinessDayClock.UtcNow()
            };
        }

        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        var serverNowUtc = BusinessDayClock.UtcNow();
        if (terminal == null)
        {
            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                ServerUtcNow = serverNowUtc
            };
        }

        var status = BusinessDayClock.EvaluateLock(
            terminalId: terminal.TerminalId,
            closedBusinessDate: terminal.ClosedBusinessDate,
            lockedByCashier: terminal.ClosingLockedByCashier,
            lockedAtUtc: terminal.ClosingLockedAtUtc,
            unlockAtUtc: terminal.ClosingUnlockAtUtc,
            candidateCashierUsername: cashierUsername,
            authoritativeServerUtc: serverNowUtc);

        if (!status.IsTerminalLocked && !string.IsNullOrWhiteSpace(terminal.ClosedBusinessDate))
        {
            terminal.ClosedBusinessDate = null;
            terminal.ClosingLockedByCashier = null;
            terminal.ClosingLockedAtUtc = null;
            terminal.ClosingUnlockAtUtc = null;
            terminal.UpdatedAt = serverNowUtc;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch
            {
                // Non-fatal if concurrent request already cleared expired lock state
            }
        }

        return status;
    }

    public static async Task<TerminalClosingLockDto> LockTerminalOnCloseAsync(
        PosDbContext db,
        int terminalId,
        string cashierId,
        DateTime closedAtUtc,
        CancellationToken ct = default)
    {
        var utcClosed = closedAtUtc.Kind == DateTimeKind.Utc
            ? closedAtUtc
            : DateTime.SpecifyKind(closedAtUtc, DateTimeKind.Utc);
        var serverNowUtc = BusinessDayClock.UtcNow();
        var closedBusinessDate = BusinessDayClock.GetBusinessDateString(utcClosed);
        var currentBusinessDate = BusinessDayClock.GetBusinessDateString(serverNowUtc);
        var unlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(utcClosed);

        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal != null &&
            string.CompareOrdinal(closedBusinessDate, currentBusinessDate) >= 0 &&
            serverNowUtc < unlockAtUtc)
        {
            terminal.ClosedBusinessDate = closedBusinessDate;
            terminal.ClosingLockedByCashier = cashierId;
            terminal.ClosingLockedAtUtc = utcClosed;
            terminal.ClosingUnlockAtUtc = unlockAtUtc;
            terminal.UpdatedAt = serverNowUtc;

            var activeSessions = await db.CashierSessions
                .Where(s => s.TerminalId == terminalId && s.Status == "ACTIVE")
                .ToListAsync(ct);
            foreach (var session in activeSessions)
            {
                session.Status = "LOGGED_OUT";
                session.LogoutAt = utcClosed;
                session.LogoutReason = "Cashier closing completed; terminal locked until 4:00 AM";
            }
        }

        return BusinessDayClock.EvaluateLock(
            terminalId: terminalId,
            closedBusinessDate: terminal?.ClosedBusinessDate,
            lockedByCashier: terminal?.ClosingLockedByCashier,
            lockedAtUtc: terminal?.ClosingLockedAtUtc,
            unlockAtUtc: terminal?.ClosingUnlockAtUtc,
            candidateCashierUsername: cashierId,
            authoritativeServerUtc: serverNowUtc);
    }
}
