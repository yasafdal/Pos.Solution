using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;

namespace POS.Client.Tests;

internal static class ZReadingLocalReadinessTests
{
    public static async Task RunAsync()
    {
        // The caller sets a scratch-only database override before this check.
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        var blockers = await ZReadingLocalReadiness.GetBlockersAsync(db, 91);
        if (blockers.Count != 0) throw new Exception("An empty isolated terminal should be locally ready.");
        db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "Order", EntityId = "pending-sale", PayloadJson = "{}", Status = "NEEDS_ATTENTION" });
        await db.SaveChangesAsync();
        if (!(await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Any())
            throw new Exception("Conflict outboxes must block finalization even when not due for upload.");
        db.SyncOutboxes.RemoveRange(db.SyncOutboxes);
        db.Orders.Add(new LocalOrder { OrderId = "unacknowledged", InvoiceNumber = "LOCAL-1", TerminalId = 91, CashierId = "test" });
        await db.SaveChangesAsync();
        if (!(await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Any())
            throw new Exception("A local sale with a missing outbox acknowledgement must block finalization.");
        db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "Order", EntityId = "unacknowledged", PayloadJson = "{}", Status = "SYNCED" });
        await db.SaveChangesAsync();
        if ((await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Count != 0)
            throw new Exception("Acknowledged sales should not block finalization.");
        db.CashDrawerShifts.Add(new LocalCashDrawerShift { ShiftId = 99, TerminalId = 91, BranchId = 1, CashierId = "test", Status = "OPEN" });
        await db.SaveChangesAsync();
        if (!(await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Any())
            throw new Exception("A local open shift must block finalization.");
        var shift = await db.CashDrawerShifts.SingleAsync(s => s.ShiftId == 99);
        shift.Status = "CLOSED";
        db.CashDrawerMovements.Add(new LocalCashDrawerMovement { MovementId = 99, TerminalId = 91, ShiftId = 99, SyncStatus = "CONFLICT" });
        await db.SaveChangesAsync();
        if (!(await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Any())
            throw new Exception("Unsynchronized movement without an outbox must not be overlooked.");
        var movement = await db.CashDrawerMovements.SingleAsync(m => m.MovementId == 99);
        movement.SyncStatus = "SYNCED";
        await db.SaveChangesAsync();
        if ((await ZReadingLocalReadiness.GetBlockersAsync(db, 91)).Count != 0)
            throw new Exception("Closed/synchronized local terminal must be locally ready.");
    }
}
