using Microsoft.EntityFrameworkCore;
using POS.Client.Data;

namespace POS.Client.Services;

/// <summary>Local checks supplement, never replace, authoritative server readiness.</summary>
public static class ZReadingLocalReadiness
{
    public static async Task<List<string>> GetBlockersAsync(LocalPosDbContext db, int terminalId)
    {
        var blockers = new List<string>();
        // Check all statuses, not only retry-due items; payloads can belong to earlier sessions.
        int pending = await db.SyncOutboxes.AsNoTracking().CountAsync(o => o.Status != "SYNCED");
        if (pending > 0) blockers.Add($"{pending} local outbox record(s) remain unsynchronized or unresolved.");
        if (await db.CashDrawerShifts.AsNoTracking().AnyAsync(s => s.TerminalId == terminalId && s.Status == "OPEN"))
            blockers.Add("Close and synchronize the local cashier shift before finalization.");
        if (await db.CashDrawerMovements.AsNoTracking().AnyAsync(m => m.TerminalId == terminalId && m.SyncStatus != "SYNCED"))
            blockers.Add("Local cash movements remain unsynchronized.");
        var acknowledgedIds = db.SyncOutboxes.Where(o => o.EntityType == "Order" && o.Status == "SYNCED").Select(o => o.EntityId);
        if (await db.Orders.AsNoTracking().AnyAsync(o => o.TerminalId == terminalId && !acknowledgedIds.Contains(o.OrderId)))
            blockers.Add("Local sales do not all have synchronized outbox acknowledgements. Resolve them without deleting payloads.");
        return blockers;
    }
}
