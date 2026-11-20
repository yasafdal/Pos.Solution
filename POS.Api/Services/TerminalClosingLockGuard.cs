using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Api.Services;

/// <summary>
/// Enforces strict separation between:
/// 1) Cashier-level Close Cashier restriction (CashierId + TerminalId + BusinessDate), and
/// 2) Terminal-level Z-Reading finalization lock (TerminalId + BusinessDate).
/// Both automatically reset at the 4:00 AM Asia/Manila Business Date boundary.
/// </summary>
public static class TerminalClosingLockGuard
{
    public static async Task<TerminalClosingLockDto> EvaluateTerminalLockAsync(
        PosDbContext db,
        int terminalId,
        string? cashierUsername = null,
        CancellationToken ct = default)
    {
        var serverNowUtc = BusinessDayClock.UtcNow();
        if (terminalId <= 0)
        {
            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                CurrentBusinessDate = BusinessDayClock.GetBusinessDateString(serverNowUtc),
                ServerUtcNow = serverNowUtc
            };
        }

        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal == null)
        {
            return new TerminalClosingLockDto
            {
                TerminalId = terminalId,
                CurrentBusinessDate = BusinessDayClock.GetBusinessDateString(serverNowUtc),
                ServerUtcNow = serverNowUtc
            };
        }

        string currentBusinessDate = BusinessDayClock.GetBusinessDateString(serverNowUtc);

        // Load active cashier closing restrictions for this terminal and current business date
        var activeRestrictions = await db.CashierClosingRestrictions
            .AsNoTracking()
            .Where(r => r.TerminalId == terminalId
                && r.BusinessDate == currentBusinessDate
                && r.Status == "ACTIVE"
                && r.UnlockAtUtc > serverNowUtc)
            .OrderByDescending(r => r.ClosedAtUtc)
            .ToListAsync(ct);

        var status = BusinessDayClock.EvaluateLock(
            terminalId: terminal.TerminalId,
            closedBusinessDate: terminal.ClosedBusinessDate,
            lockedByCashier: terminal.ClosingLockedByCashier,
            lockedAtUtc: terminal.ClosingLockedAtUtc,
            unlockAtUtc: terminal.ClosingUnlockAtUtc,
            candidateCashierUsername: cashierUsername,
            authoritativeServerUtc: serverNowUtc,
            additionalClosedCashiersToday: activeRestrictions.Select(r => r.CashierId));

        status.ActiveCashierRestrictions = activeRestrictions.Select(MapRestrictionToDto).ToList();

        // Enforce single active open cashier shift ownership per terminal using CashDrawerShifts as source of truth
        var activeOpenShift = await db.CashDrawerShifts
            .AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .ThenByDescending(s => s.ShiftId)
            .FirstOrDefaultAsync(ct);

        if (activeOpenShift != null)
        {
            status.ActiveShiftOwnerCashierId = activeOpenShift.CashierId;
            status.ActiveShiftId = activeOpenShift.ShiftId;
            if (!string.IsNullOrWhiteSpace(cashierUsername) &&
                !string.Equals(activeOpenShift.CashierId, cashierUsername.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                status.IsShiftOwnedByAnotherCashier = true;
                status.ActiveShiftOwnershipMessage = TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage;
            }
        }

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

    /// <summary>
    /// Called ONLY by Close Cashier (CloseShift).
    /// Restricts ONLY the closing cashier on this terminal for the closed Business Date (CashierId + TerminalId + BusinessDate).
    /// Does NOT lock the terminal for other cashiers and does NOT block the same cashier on other terminals.
    /// </summary>
    public static async Task<TerminalClosingLockDto> RestrictCashierOnCloseAsync(
        PosDbContext db,
        int branchId,
        int terminalId,
        string cashierId,
        long? shiftId,
        string? clientShiftId,
        string? clientCloseId,
        DateTime closedAtUtc,
        CancellationToken ct = default)
    {
        var utcClosed = BusinessDayClock.NormalizeUtc(closedAtUtc);
        var serverNowUtc = BusinessDayClock.UtcNow();
        var closedBusinessDate = BusinessDayClock.GetBusinessDateString(utcClosed);
        var unlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(utcClosed);

        // Idempotent upsert: avoid duplicate restriction rows on retries or outbox replays
        CashierClosingRestriction? existing = null;
        if (!string.IsNullOrWhiteSpace(clientCloseId))
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId && r.ClientCloseId == clientCloseId, ct);
        }
        if (existing == null && shiftId.HasValue && shiftId.Value > 0)
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId && r.ShiftId == shiftId.Value, ct);
        }
        if (existing == null)
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId
                     && r.BusinessDate == closedBusinessDate
                     && r.Status == "ACTIVE"
                     && r.CashierId.ToLower() == cashierId.Trim().ToLower(), ct);
        }

        if (existing == null)
        {
            db.CashierClosingRestrictions.Add(new CashierClosingRestriction
            {
                BranchId = branchId,
                TerminalId = terminalId,
                CashierId = cashierId.Trim(),
                BusinessDate = closedBusinessDate,
                ShiftId = shiftId,
                ClientShiftId = clientShiftId,
                ClientCloseId = clientCloseId,
                ClosedAtUtc = utcClosed,
                UnlockAtUtc = unlockAtUtc,
                Status = "ACTIVE",
                CreatedAtUtc = serverNowUtc
            });
        }
        else if (existing.Status == "ACTIVE")
        {
            existing.ShiftId ??= shiftId;
            if (string.IsNullOrWhiteSpace(existing.ClientShiftId)) existing.ClientShiftId = clientShiftId;
            if (string.IsNullOrWhiteSpace(existing.ClientCloseId)) existing.ClientCloseId = clientCloseId;
        }

        // End ONLY this closing cashier's active session(s) on this terminal; leave other cashiers untouched
        var closingCashierSessions = await db.CashierSessions
            .Where(s => s.TerminalId == terminalId
                        && s.Status == "ACTIVE"
                        && s.CashierId.ToLower() == cashierId.Trim().ToLower())
            .ToListAsync(ct);
        foreach (var session in closingCashierSessions)
        {
            session.Status = "LOGGED_OUT";
            session.LogoutAt = utcClosed;
            session.LogoutReason = $"Close Cashier completed for business date {closedBusinessDate}";
        }

        return await EvaluateTerminalLockAsync(db, terminalId, cashierId, ct);
    }

    /// <summary>
    /// Called ONLY when Z-Reading is successfully saved and finalized (inside the same DB transaction).
    /// Locks the entire terminal for the finalized Business Date (TerminalId + BusinessDate) until 4:00 AM next Business Date.
    /// </summary>
    public static async Task<TerminalClosingLockDto> LockTerminalOnZReadingAsync(
        PosDbContext db,
        int terminalId,
        DateOnly businessDate,
        string finalizedBy,
        DateTime finalizedAtUtc,
        CancellationToken ct = default)
    {
        var utcFinalized = BusinessDayClock.NormalizeUtc(finalizedAtUtc);
        var serverNowUtc = BusinessDayClock.UtcNow();
        string finalizedDateStr = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string currentBusinessDateStr = BusinessDayClock.GetBusinessDateString(serverNowUtc);
        DateTime unlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(businessDate);

        var terminal = await db.Terminals.FirstOrDefaultAsync(t => t.TerminalId == terminalId, ct);
        if (terminal != null &&
            string.CompareOrdinal(finalizedDateStr, currentBusinessDateStr) >= 0 &&
            serverNowUtc < unlockAtUtc)
        {
            terminal.ClosedBusinessDate = finalizedDateStr;
            terminal.ClosingLockedByCashier = finalizedBy;
            terminal.ClosingLockedAtUtc = utcFinalized;
            terminal.ClosingUnlockAtUtc = unlockAtUtc;
            terminal.UpdatedAt = serverNowUtc;

            var activeSessions = await db.CashierSessions
                .Where(s => s.TerminalId == terminalId && s.Status == "ACTIVE")
                .ToListAsync(ct);
            foreach (var session in activeSessions)
            {
                session.Status = "LOGGED_OUT";
                session.LogoutAt = utcFinalized;
                session.LogoutReason = $"Z-Reading finalized for business date {finalizedDateStr}; terminal locked until 4:00 AM";
            }
        }

        return BusinessDayClock.EvaluateLock(
            terminalId: terminalId,
            closedBusinessDate: terminal?.ClosedBusinessDate,
            lockedByCashier: terminal?.ClosingLockedByCashier,
            lockedAtUtc: terminal?.ClosingLockedAtUtc,
            unlockAtUtc: terminal?.ClosingUnlockAtUtc,
            candidateCashierUsername: finalizedBy,
            authoritativeServerUtc: serverNowUtc);
    }

    /// <summary>
    /// Authorized supervisor correction/reopening workflow for a cashier restricted by Close Cashier.
    /// Preserves all historical CashDrawerShift accountability records immutably and records full reopen audit trail.
    /// </summary>
    public static async Task<CashierClosingRestrictionDto?> ReopenCashierRestrictionAsync(
        PosDbContext db,
        int terminalId,
        string cashierId,
        string businessDate,
        string reopenedBy,
        string reason,
        string? clientOperationId = null,
        CancellationToken ct = default)
    {
        var nowUtc = BusinessDayClock.UtcNow();
        if (!string.IsNullOrWhiteSpace(clientOperationId))
        {
            var idempotent = await db.CashierClosingRestrictions
                .FirstOrDefaultAsync(r => r.TerminalId == terminalId && r.ClientOperationId == clientOperationId, ct);
            if (idempotent != null)
                return MapRestrictionToDto(idempotent);
        }

        var normalizedCashier = cashierId.Trim().ToLowerInvariant();
        var activeRows = await db.CashierClosingRestrictions
            .Where(r => r.TerminalId == terminalId
                        && r.BusinessDate == businessDate
                        && r.Status == "ACTIVE"
                        && r.CashierId.ToLower() == normalizedCashier)
            .ToListAsync(ct);

        if (activeRows.Count == 0)
            return null;

        foreach (var row in activeRows)
        {
            row.Status = "REOPENED";
            row.ReopenedBy = reopenedBy.Trim();
            row.ReopenedAtUtc = nowUtc;
            row.ReopenReason = reason.Trim();
            if (!string.IsNullOrWhiteSpace(clientOperationId))
                row.ClientOperationId = clientOperationId.Trim();
        }

        await db.SaveChangesAsync(ct);
        return MapRestrictionToDto(activeRows[0]);
    }

    public static CashierClosingRestrictionDto MapRestrictionToDto(CashierClosingRestriction r) => new()
    {
        RestrictionId = r.RestrictionId,
        BranchId = r.BranchId,
        TerminalId = r.TerminalId,
        CashierId = r.CashierId,
        BusinessDate = r.BusinessDate,
        ShiftId = r.ShiftId,
        ClientShiftId = r.ClientShiftId,
        ClientCloseId = r.ClientCloseId,
        ClosedAtUtc = r.ClosedAtUtc,
        UnlockAtUtc = r.UnlockAtUtc,
        Status = r.Status,
        ReopenedBy = r.ReopenedBy,
        ReopenedAtUtc = r.ReopenedAtUtc,
        ReopenReason = r.ReopenReason
    };
}
