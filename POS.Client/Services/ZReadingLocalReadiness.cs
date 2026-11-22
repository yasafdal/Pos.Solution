using Microsoft.EntityFrameworkCore;
using POS.Client.Data;

namespace POS.Client.Services;

/// <summary>Local checks supplement, never replace, authoritative server readiness.</summary>
public static class ZReadingLocalReadiness
{
    public static async Task<List<string>> GetBlockersAsync(
        LocalPosDbContext db,
        int terminalId,
        DateOnly? businessDate = null)
    {
        var blockers = new List<string>();
        // Check all statuses, not only retry-due items; payloads can belong to earlier sessions.
        int pending = await db.SyncOutboxes.AsNoTracking().CountAsync(o => o.Status != "SYNCED");
        if (pending > 0) blockers.Add($"{pending} local outbox record(s) remain unsynchronized or unresolved.");
        var localOpenShift = await db.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync();
        if (localOpenShift != null)
        {
            var nowUtc = POS.Shared.Services.BusinessDayClock.UtcNow();
            if (POS.Shared.Services.BusinessDayClock.IsPreviousBusinessDateShift(localOpenShift.OpenedAt, nowUtc, localOpenShift.BusinessDate))
            {
                string origDate = POS.Shared.Services.BusinessDayClock.GetShiftBusinessDateString(localOpenShift.OpenedAt, localOpenShift.BusinessDate);
                blockers.Add($"Unresolved cashier shift from previous Business Date {origDate} (cashier {localOpenShift.CashierId}) requires supervisor overnight closing before Z-Reading.");
            }
            else
            {
                blockers.Add("Close and synchronize the local cashier shift before finalization.");
            }
        }
        var localClosedShifts = await db.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "CLOSED")
            .ToListAsync();
        if (localClosedShifts.Count > 0)
        {
            var positiveClientShiftIds = localClosedShifts
                .Where(s => s.ShiftId > 0 && !string.IsNullOrWhiteSpace(s.ClientShiftId))
                .Select(s => s.ClientShiftId!)
                .ToHashSet(StringComparer.Ordinal);
            var positiveClientCloseIds = localClosedShifts
                .Where(s => s.ShiftId > 0 && !string.IsNullOrWhiteSpace(s.ClientCloseId))
                .Select(s => s.ClientCloseId!)
                .ToHashSet(StringComparer.Ordinal);

            bool hasUnaccountedLocalClose = localClosedShifts.Any(s =>
            {
                // Ignore retired open-shift negative placeholders or server-retired cache rows that were never closed locally on this client.
                if (string.IsNullOrWhiteSpace(s.ClientCloseId) && string.IsNullOrWhiteSpace(s.RecoveryOperationId))
                    return false;

                // Ignore negative offline placeholder rows already superseded by a positive server shift row.
                if (s.ShiftId < 0 &&
                    ((!string.IsNullOrWhiteSpace(s.ClientShiftId) && positiveClientShiftIds.Contains(s.ClientShiftId)) ||
                     (!string.IsNullOrWhiteSpace(s.ClientCloseId) && positiveClientCloseIds.Contains(s.ClientCloseId))))
                    return false;

                // When checking a specific Business Date, only inspect local closes belonging to that Business Date.
                if (businessDate.HasValue &&
                    POS.Shared.Services.BusinessDayClock.GetShiftBusinessDate(s.OpenedAt, s.BusinessDate) != businessDate.Value)
                    return false;

                return !s.ActualCountedBalance.HasValue ||
                       (string.IsNullOrEmpty(s.ClosingDenominationsJson) && string.IsNullOrEmpty(s.ClosingOriginalDeclarationJson));
            });

            if (hasUnaccountedLocalClose)
                blockers.Add("One or more closed local shifts are missing required cash accountability (denomination count/declaration).");
        }
        if (await db.CashDrawerMovements.AsNoTracking().AnyAsync(m => m.TerminalId == terminalId && m.SyncStatus != "SYNCED"))
            blockers.Add("Local cash movements remain unsynchronized.");
        if (await db.TransactionVoids.AsNoTracking().AnyAsync(v => v.TerminalId == terminalId && v.SyncStatus != "SYNCED"))
            blockers.Add("Local offline transaction voids remain unsynchronized.");
        var acknowledgedIds = db.SyncOutboxes.Where(o => o.EntityType == "Order" && o.Status == "SYNCED").Select(o => o.EntityId);
        if (await db.Orders.AsNoTracking().AnyAsync(o => o.TerminalId == terminalId && !acknowledgedIds.Contains(o.OrderId)))
            blockers.Add("Local sales do not all have synchronized outbox acknowledgements. Resolve them without deleting payloads.");
        return blockers;
    }
}
