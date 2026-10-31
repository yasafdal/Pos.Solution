using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Services;

public partial class ZReadingService(PosDbContext db, IConfiguration configuration)
{
    public bool Enabled => configuration.GetValue<bool>("ZReading:Enabled");

    private void RequireEnabled()
    {
        if (!Enabled) throw new ZReadingException(409, "Z-Reading is disabled; deploy and explicitly configure it first.");
    }

    public async Task<ZReadingReadinessDto> ReadinessAsync(int terminalId, DateOnly date, CancellationToken ct = default)
    {
        var (start, end) = Period(date);
        var result = new ZReadingReadinessDto { TerminalId = terminalId, BusinessDate = date, PeriodStartUtc = start, PeriodEndUtc = end, Enabled = Enabled };
        if (!Enabled) { result.Blockers.Add("Z-Reading is disabled."); return result; }
        if (db.Database.IsNpgsql())
        {
            var guardInstalled = await db.Database.SqlQueryRaw<bool>("SELECT to_regprocedure('public.pos_z_lock_terminal(integer)') IS NOT NULL AS \"Value\"").SingleAsync(ct);
            if (!guardInstalled) result.Blockers.Add("Required finalization-barrier SQL function public.pos_z_lock_terminal(integer) is not installed.");
        }
        var existing = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.BusinessDate == date, ct);
        if (existing != null) { result.AlreadyGenerated = true; result.ExistingReportId = existing.Id; return result; }
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == terminalId, ct);
        if (terminal == null || !terminal.IsActive) result.Blockers.Add("Terminal is missing or inactive.");
        var state = await db.ZReadingTerminalStates.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == terminalId, ct);
        if (state == null) result.Blockers.Add("Verified first business date and opening accumulated gross sales have not been provisioned.");
        else
        {
            result.NextBusinessDate = state.LastBusinessDate?.AddDays(1) ?? state.FirstBusinessDate;
            if (date != result.NextBusinessDate) result.Blockers.Add($"Generate sequentially; next business date is {result.NextBusinessDate:yyyy-MM-dd}.");
            if (state.OpeningAccumulatedSales < 0 || state.AccumulatedGrossSales < state.OpeningAccumulatedSales) result.Blockers.Add("Invalid provisioned accumulated sales state.");
        }
        if (end > DateTime.UtcNow && !configuration.GetValue<bool>("ZReading:BypassPeriodEndForTesting"))
            result.Blockers.Add("Business period has not ended.");
        if (await db.CashDrawerShifts.AnyAsync(x => x.TerminalId == terminalId && x.OpenedAt < end &&
            (x.Status == "OPEN" || x.ClosedAt == null || (x.ClosedAt > start && (x.OpenedAt < start || x.ClosedAt > end))), ct))
            result.Blockers.Add("Open, incomplete, or cross-period shift requires reviewed closure/handoff.");
        if (await db.CashDrawerMovements.AnyAsync(x => x.TerminalId == terminalId && x.CreatedAt < end &&
            (x.Status == "PENDING_APPROVAL" || x.Status == "FAILED_CONFLICT" || x.Status == "NEEDS_ATTENTION"), ct))
            result.Blockers.Add("Cash movement approval or synchronization conflict is unresolved.");
        if (await db.HeldSales.AnyAsync(x => x.TerminalId == terminalId && x.CreatedAt < end && x.Status == "HELD", ct))
            result.Blockers.Add("Held sales must be resolved.");
        var orders = await db.Orders.AsNoTracking().Include(x => x.Payments).Where(x => x.TerminalId == terminalId && x.CreatedAt < end).ToListAsync(ct);
        if (orders.Any(x => x.OrderStatus != "CANCELLED" && x.OrderStatus != "VOIDED" &&
            (x.PaymentStatus != "PAID" && x.PaymentStatus != "REFUNDED" && x.PaymentStatus != "PARTIALLY_REFUNDED")))
            result.Blockers.Add("Unpaid or incomplete orders remain.");
        if (orders.Any(x => x.OrderStatus != "CANCELLED" && x.OrderStatus != "VOIDED" && x.OrderStatus != "COMPLETED" && x.OrderStatus != "RETURNED" && x.OrderStatus != "PARTIALLY_RETURNED"))
            result.Blockers.Add("Incomplete order lifecycle remains, regardless of paid flag.");
        if (orders.Any(x => x.InvoiceIssuedAt < end && x.InvoiceIssuedAt.HasValue &&
            (Math.Abs(x.Payments.Where(p => p.PaymentStatus == "SUCCESS" || p.PaymentStatus == "REFUNDED").Sum(p => p.Amount) - x.TotalAmount) > .01m ||
             x.Payments.Any(p => (p.PaymentStatus == "SUCCESS" || p.PaymentStatus == "REFUNDED") && BusinessDate(p.PaidAt) != BusinessDate(x.InvoiceIssuedAt.Value)))))
            result.Blockers.Add("Invoice totals/payment entries do not reconcile, or collections cross the invoice business date; review event attribution before finalization.");
        if (orders.Any(x => x.InvoiceIssuedAt < end && x.InvoiceIssuedAt.HasValue &&
            (x.Subtotal - x.DiscountAmount != x.TotalAmount)))
            result.Warnings.Add("Stored VAT removal/residual is reported separately as VatAdjustment; cashier net uses stored order totals.");
        if (orders.Any(x => x.Payments.Any(p => p.PaidAt < end && p.PaymentStatus != "SUCCESS" && p.PaymentStatus != "REFUNDED" && p.PaymentStatus != "FAILED" && p.PaymentStatus != "CANCELLED")))
            result.Blockers.Add("Unresolved payment commits remain.");
        if (orders.Any(x => x.Payments.Any(p => p.PaymentStatus == "SUCCESS" || p.PaymentStatus == "REFUNDED") &&
            (!x.InvoiceIssuedAt.HasValue || string.IsNullOrEmpty(x.InvoiceNumber))))
            result.Blockers.Add("Paid order has no authoritative invoice issuance timestamp/number; reconcile before finalization.");
        if (await db.SalesReturns.AnyAsync(x => x.TerminalId == terminalId && x.CreatedAt < end &&
            (x.Status != "COMPLETED" || x.CompletedAt == null), ct)) result.Blockers.Add("Unresolved return remains.");
        if (await db.Refunds.AnyAsync(x => x.OriginalPayment != null && x.OriginalPayment.Order.TerminalId == terminalId && x.RequestedAt < end && x.Status != "COMPLETED", ct))
            result.Blockers.Add("Unresolved refund remains.");
        result.Warnings.Add("API cannot inspect another device's outboxes. Drain all queues and explicitly confirm sole-device completeness on the authenticated current terminal.");
        result.IsReady = result.Blockers.Count == 0;
        return result;
    }

    public async Task<ZReadingReportDto?> GetAsync(int terminalId, DateOnly date, CancellationToken ct = default)
    {
        RequireEnabled();
        var row = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == terminalId && x.BusinessDate == date, ct);
        return row == null ? null : ReadSnapshot(row);
    }

    private static ZReadingReportDto ReadSnapshot(ZReading row)
    {
        if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.SnapshotJson))) != row.SnapshotSha256)
            throw new ZReadingException(409, "Stored snapshot integrity check failed.");
        return JsonSerializer.Deserialize<ZReadingReportDto>(row.SnapshotJson) ?? throw new ZReadingException(409, "Stored snapshot cannot be read.");
    }

    public async Task<ZReadingReportDto> GenerateAsync(GenerateZReadingRequest request, string generatedBy, CancellationToken ct = default)
    {
        RequireEnabled();
        if (string.IsNullOrWhiteSpace(request.ClientOperationId) || request.ClientOperationId.Length > 100 || string.IsNullOrWhiteSpace(generatedBy))
            throw new ZReadingException(400, "Stable client operation ID and authenticated operator are required.");
        if (!request.AllLocalOutboxesDrained || !request.NoUnresolvedLocalTransactions || !request.SoleDeviceCompletenessConfirmed || string.IsNullOrWhiteSpace(request.DeviceIdentifier) || request.DeviceIdentifier.Length > 100)
            throw new ZReadingException(409, "All local outboxes, unresolved transactions and sole-device completeness must be explicitly confirmed.");
        await using var tx = await db.Database.BeginTransactionAsync(db.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable, ct);
        await ShiftLifecycle.LockTerminalAsync(db, request.TerminalId, ct);
        var operation = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.ClientOperationId == request.ClientOperationId, ct);
        if (operation != null)
        {
            if (operation.TerminalId != request.TerminalId || operation.BusinessDate != request.BusinessDate || operation.DeviceIdentifier != request.DeviceIdentifier || operation.GeneratedBy != generatedBy)
                throw new ZReadingException(409, "Client operation ID belongs to a different request.");
            return ReadSnapshot(operation);
        }
        var existing = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == request.TerminalId && x.BusinessDate == request.BusinessDate, ct);
        if (existing != null) return ReadSnapshot(existing);
        var ready = await ReadinessAsync(request.TerminalId, request.BusinessDate, ct);
        if (!ready.IsReady) throw new ZReadingException(409, string.Join(" ", ready.Blockers));
        var state = await db.ZReadingTerminalStates.SingleAsync(x => x.TerminalId == request.TerminalId, ct);
        await db.Entry(state).ReloadAsync(ct);
        // Shared write fence invalidates a writer's stale SERIALIZABLE snapshot after it waited on the terminal.
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT public.pos_z_lock_terminal({request.TerminalId})", ct);
            await db.Entry(state).ReloadAsync(ct);
        }
        else
        {
            state.MutationVersion = checked(state.MutationVersion + 1);
            await db.SaveChangesAsync(ct);
        }
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(x => x.TerminalId == request.TerminalId, ct);
        var report = new ZReadingReportDto
        {
            Id = Guid.NewGuid(), TerminalId = terminal.TerminalId, BranchId = terminal.BranchId, OutletId = terminal.OutletId,
            TerminalCode = terminal.TerminalCode, TerminalName = terminal.Name,
            BusinessDate = request.BusinessDate, PeriodStartUtc = ready.PeriodStartUtc, PeriodEndUtc = ready.PeriodEndUtc,
            GeneratedAt = DateTime.UtcNow, GeneratedBy = generatedBy, ClientOperationId = request.ClientOperationId,
            ZNumber = checked(state.LastZNumber + 1), ResetCounter = state.ResetCounter, PreviousAccumulatedSales = state.AccumulatedGrossSales
        };
        await AggregateAsync(report, ct);
        report.PresentAccumulatedSales = report.PreviousAccumulatedSales + report.GrossSales;
        var json = JsonSerializer.Serialize(report);
        db.ZReadings.Add(new ZReading
        {
            Id = report.Id, TerminalId = terminal.TerminalId, BranchId = terminal.BranchId, OutletId = terminal.OutletId,
            BusinessDate = report.BusinessDate, PeriodStartUtc = report.PeriodStartUtc, PeriodEndUtc = report.PeriodEndUtc,
            GeneratedAt = report.GeneratedAt, GeneratedBy = generatedBy, ClientOperationId = request.ClientOperationId,
            DeviceIdentifier = request.DeviceIdentifier, ZNumber = report.ZNumber!.Value, SnapshotJson = json,
            SnapshotSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
        });
        state.LastZNumber = report.ZNumber.Value;
        state.LastBusinessDate = report.BusinessDate;
        state.AccumulatedGrossSales = report.PresentAccumulatedSales;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return report;
    }

    // Manila is UTC+08 throughout the supported fiscal dates; never use host Local timezone.
    public static DateOnly BusinessDate(DateTime utc) => DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(4));
    public static (DateTime StartUtc, DateTime EndUtc) Period(DateOnly date)
    {
        var start = DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(4, 0)).AddHours(-8), DateTimeKind.Utc);
        return (start, start.AddDays(1));
    }
}
