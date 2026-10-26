using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;

namespace POS.Client.Services;

public static class ShiftSession
{
    public static async Task<LocalCashDrawerShift?> CurrentAsync(HttpClient http, int terminalId, bool offline)
    {
        if (!offline)
        {
            var response = await http.GetAsync($"api/cash-drawer/current?terminalId={terminalId}");
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto != null)
                {
                    if (dto.TerminalId != terminalId || dto.Status != "OPEN") throw new InvalidOperationException("Current shift response does not match this drawer.");
                    return await CacheAsync(dto, reconcileCurrent: true);
                }
                throw new InvalidOperationException("Current shift response is empty.");
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException("Unable to verify current shift.");
            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext(); db.InitializeDatabase();
                // A negative ID is a locally confirmed opening still awaiting acknowledgement.
                var pending = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN" && s.ShiftId < 0);
                if (pending != null) return pending;
                foreach (var stale in await db.CashDrawerShifts.Where(s => s.TerminalId == terminalId && s.Status == "OPEN").ToListAsync()) stale.Status = "CLOSED";
                await db.SaveChangesAsync(); return null;
            }
            finally { LocalPosDbContext.WriteLock.Release(); }
        }
        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        return await local.CashDrawerShifts.AsNoTracking().SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN");
    }

    public static async Task<LocalCashDrawerShift> OpenAsync(HttpClient http, OpenShiftDto request, string cashier, bool offline)
    {
        if (!request.OpeningFloat.HasValue || request.OpeningFloat < 0) throw new InvalidOperationException("Enter opening float. Zero is allowed.");
        request.CashierId = cashier;
        if (string.IsNullOrWhiteSpace(request.ClientShiftId)) throw new InvalidOperationException("Opening identity is required.");
        if (!offline)
        {
            var response = await http.PostAsJsonAsync("api/cash-drawer/open-shift", request);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            return await CacheAsync((await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>())!);
        }
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();
            if (!await db.TerminalConfigs.AnyAsync(t => t.TerminalId == request.TerminalId && t.BranchId == request.BranchId && t.OutletId > 0)) throw new InvalidOperationException("Refresh terminal ownership online before opening offline.");
            var existing = await db.CashDrawerShifts.SingleOrDefaultAsync(s => s.TerminalId == request.TerminalId && s.Status == "OPEN");
            if (existing != null) throw new InvalidOperationException("This drawer already has an open shift.");
            var shift = new LocalCashDrawerShift { ShiftId = -DateTime.UtcNow.Ticks, BranchId = request.BranchId, TerminalId = request.TerminalId, CashierId = cashier, OpeningConfirmed = true, ClientShiftId = request.ClientShiftId, OpeningFloat = Math.Round(request.OpeningFloat.Value, 2), ExpectedBalance = Math.Round(request.OpeningFloat.Value, 2), OpenedAt = request.OpenedAt ?? DateTime.UtcNow };
            db.CashDrawerShifts.Add(shift);
            db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "SHIFT_OPEN", EntityId = shift.ClientShiftId!, PayloadJson = System.Text.Json.JsonSerializer.Serialize(request), Status = "PENDING", CreatedAt = shift.OpenedAt });
            await db.SaveChangesAsync(); await tx.CommitAsync(); return shift;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    public static async Task<LocalCashDrawerShift> CacheAsync(CashDrawerShiftDto dto, bool reconcileCurrent = false)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var transaction = await db.Database.BeginTransactionAsync();
            var shift = await ApplySnapshotAsync(db, dto, reconcileCurrent);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return shift;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    // Caller owns WriteLock and transaction, allowing reference-data refresh to share this exact merge.
    internal static async Task<LocalCashDrawerShift> ApplySnapshotAsync(LocalPosDbContext db, CashDrawerShiftDto dto, bool reconcileCurrent = false)
    {
        var shift = await db.CashDrawerShifts.FindAsync(dto.ShiftId);
        if (shift == null) { shift = new LocalCashDrawerShift { ShiftId = dto.ShiftId }; db.CashDrawerShifts.Add(shift); }
        // Historical opening ACKs never retire a different opening. Only an authoritative
        // current endpoint can retire other acknowledged shifts on this terminal.
        foreach (var old in await db.CashDrawerShifts.Where(s => s.TerminalId == dto.TerminalId && s.ShiftId != dto.ShiftId && s.Status == "OPEN").ToListAsync())
            if ((old.ShiftId < 0 && !string.IsNullOrWhiteSpace(dto.ClientShiftId) && old.ClientShiftId == dto.ClientShiftId)
                || (reconcileCurrent && old.ShiftId > 0)) old.Status = "CLOSED";
        shift.BranchId = dto.BranchId; shift.TerminalId = dto.TerminalId; shift.CashierId = dto.CashierId;
        shift.OpeningFloat = dto.OpeningFloat; shift.OpeningConfirmed = dto.OpeningConfirmed; shift.ClientShiftId = dto.ClientShiftId;
        shift.OpenedAt = dto.OpenedAt; shift.ClosedAt = dto.ClosedAt; shift.Status = dto.Status;
        shift.TotalCashSales = dto.TotalCashSales + await UnacknowledgedCashAsync(db, dto);
        shift.TotalCashRefunds = dto.TotalCashRefunds;
        shift.TotalCashIn = dto.TotalCashIn; shift.TotalCashOut = dto.TotalCashOut; shift.TotalCashDrops = dto.TotalCashDrops;
        shift.ExpectedBalance = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
        return shift;
    }

    private static async Task<decimal> UnacknowledgedCashAsync(LocalPosDbContext db, CashDrawerShiftDto dto)
    {
        var outboxes = await db.SyncOutboxes.AsNoTracking().Where(o => o.EntityType == "Order").ToListAsync();
        var accepted = dto.AcknowledgedLocalTransactionIds?.ToHashSet(StringComparer.Ordinal);
        var pendingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outbox in outboxes)
        {
            // Snapshot IDs, not present-day outbox status, decide membership: an upload ACK
            // can arrive after the server read the totals but before this local write lock.
            if (accepted != null ? accepted.Contains(outbox.EntityId) : outbox.Status == "SYNCED") continue;
            var order = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>(outbox.PayloadJson);
            if (order == null) throw new InvalidOperationException("Cannot reconcile an unreadable offline order.");
            var matches = !string.IsNullOrWhiteSpace(order.ClientShiftId)
                ? order.ClientShiftId == dto.ClientShiftId
                : order.ShiftId == dto.ShiftId;
            if (matches && order.TerminalId == dto.TerminalId && order.BranchId == dto.BranchId) pendingIds.Add(outbox.EntityId);
        }
        if (pendingIds.Count == 0) return 0;
        if (accepted == null) throw new InvalidOperationException("Refresh against an updated server before reconciling pending cash sales; exact transaction acknowledgement IDs are required.");
        var payments = await (from payment in db.Payments.AsNoTracking()
                              join order in db.Orders.AsNoTracking() on payment.OrderId equals order.OrderId
                              where order.TerminalId == dto.TerminalId && order.BranchId == dto.BranchId && order.CashierId == dto.CashierId
                                  && payment.PaymentMethod == "CASH" && payment.PaymentStatus == "SUCCESS"
                              select payment).ToListAsync();
        return payments.Where(p => pendingIds.Contains(p.OrderId)).Sum(p => p.Amount);
    }
}
