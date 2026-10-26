using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Tests;

public static class ShiftCacheRegressionTests
{
    // The harness must redirect LocalPosDbContext to an isolated SQLite file before invoking this suite.
    public static async Task RunAsync()
    {
        using (var safety = new LocalPosDbContext())
        {
            var expected = Environment.GetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH");
            if (string.IsNullOrEmpty(expected) || !Path.GetFullPath(safety.Database.GetDbConnection().DataSource).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Shift cache tests require POS_TEST_LOCAL_DB_PATH and an isolated LocalPosDbContext.");
        }
        await HistoricalAckDoesNotCloseNewerShift();
        await QueuedOpeningOwnerRecovery();
        await ExactCashDeltaMerge();
        await ReferenceRefreshRetiresPlaceholder();
        await CurrentEndpointReconcilesAcknowledgedShifts();
        await ServerSnapshotIncludesExactIds();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static CashDrawerShiftDto Snapshot(long id, string clientId, string status = "OPEN", decimal sales = 0)
        => new() { ShiftId = id, ClientShiftId = clientId, BranchId = 1, TerminalId = 1, CashierId = "original-owner", OpeningFloat = 50, OpeningConfirmed = true, Status = status, OpenedAt = DateTime.UtcNow.AddHours(-1), TotalCashSales = sales };

    private static async Task Reset()
    {
        using var db = new LocalPosDbContext(); db.InitializeDatabase();
        await db.SyncOutboxes.ExecuteDeleteAsync(); await db.Payments.ExecuteDeleteAsync(); await db.Orders.ExecuteDeleteAsync();
        await db.CashDrawerShifts.ExecuteDeleteAsync(); await db.TerminalConfigs.ExecuteDeleteAsync();
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private static async Task QueuedOpeningOwnerRecovery()
    {
        await Reset();
        var opening = new OpenShiftDto { ClientShiftId = "legacy-owner", BranchId = 1, TerminalId = 1, OpeningFloat = 50 };
        var outbox = new LocalSyncOutbox { EntityType = "SHIFT_OPEN", EntityId = opening.ClientShiftId, PayloadJson = JsonSerializer.Serialize(opening) };
        using (var seed = new LocalPosDbContext())
        {
            seed.CashDrawerShifts.Add(new LocalCashDrawerShift { ShiftId = -2, ClientShiftId = opening.ClientShiftId, BranchId = 1, TerminalId = 1, CashierId = "original-owner", OpeningConfirmed = true, OpeningFloat = 50 });
            seed.SyncOutboxes.Add(outbox); await seed.SaveChangesAsync();
        }
        var calls = 0;
        using var http = new HttpClient(new Handler(async request =>
        {
            calls++;
            var sent = await request.Content!.ReadFromJsonAsync<OpenShiftDto>();
            Check(sent!.CashierId == "original-owner", "Legacy queued opening must recover original owner, never uploader.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Snapshot(201, opening.ClientShiftId)) };
        })) { BaseAddress = new Uri("http://test/") };
        using var db = new LocalPosDbContext();
        var service = new OfflineTransactionSyncService(db);
        Check(await service.UploadTransactionAsync(outbox, http), "Recovered legacy opening should synchronize.");
        Check(calls == 1, "Recovered opening posts once.");
        opening.CashierId = "wrong-owner"; outbox.PayloadJson = JsonSerializer.Serialize(opening);
        Check(!await service.UploadTransactionAsync(outbox, http), "Wrong queued owner is rejected.");
        Check(calls == 1, "Wrong owner must be rejected before HTTP.");
        opening.ClientShiftId = "missing-local"; opening.CashierId = null; outbox.EntityId = opening.ClientShiftId; outbox.PayloadJson = JsonSerializer.Serialize(opening);
        Check(!await service.UploadTransactionAsync(outbox, http), "Unrecoverable opening owner is rejected.");
        Check(calls == 1, "Unknown owner must never be assigned to uploader.");
        Console.WriteLine("PASS shift-cache: queued opening owner recovery and rejection");
    }

    private static async Task ExactCashDeltaMerge()
    {
        await Reset();
        using (var db = new LocalPosDbContext())
        {
            db.CashDrawerShifts.Add(new LocalCashDrawerShift { ShiftId = -10, ClientShiftId = "merge", BranchId = 1, TerminalId = 1, CashierId = "original-owner", TotalCashSales = 65 });
            foreach (var (id, amount, status, shift) in new[] { ("pending", 25m, "PENDING", "merge"), ("lost-ack", 40m, "RETRY_PENDING", "merge"), ("acked", 10m, "SYNCED", "merge"), ("foreign", 99m, "PENDING", "other") })
            {
                db.Orders.Add(new LocalOrder { OrderId = id, InvoiceNumber = id, BranchId = 1, TerminalId = 1, CashierId = "original-owner", TotalAmount = amount, PaymentStatus = "SUCCESS" });
                db.Payments.Add(new LocalPayment { OrderId = id, Amount = amount });
                db.Payments.Add(new LocalPayment { OrderId = id, Amount = 500, PaymentMethod = "CARD" });
                db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "Order", EntityId = id, Status = status, PayloadJson = JsonSerializer.Serialize(new OrderCreateDto { ShiftId = -10, ClientShiftId = shift, BranchId = 1, TerminalId = 1 }) });
            }
            await db.SaveChangesAsync();
        }
        var dto = Snapshot(300, "merge", sales: 150);
        dto.AcknowledgedLocalTransactionIds = new() { "lost-ack", "acked" };
        var cached = await ShiftSession.CacheAsync(dto);
        Check(cached.TotalCashSales == 175, $"Server 150 plus pending CASH 25 expected, actual {cached.TotalCashSales}; not max, cards, other shifts, or lost ACK.");
        Check(cached.ExpectedBalance == 225, "Expected balance includes opening float plus exact merged sales.");
        cached = await ShiftSession.CacheAsync(dto);
        Check(cached.TotalCashSales == 175, "Repeated snapshot must not add offline sale twice.");
        using (var acknowledgedAfterSnapshot = new LocalPosDbContext())
        {
            var outbox = await acknowledgedAfterSnapshot.SyncOutboxes.SingleAsync(o => o.EntityId == "pending");
            outbox.Status = "SYNCED"; await acknowledgedAfterSnapshot.SaveChangesAsync();
        }
        cached = await ShiftSession.CacheAsync(dto);
        Check(cached.TotalCashSales == 175, "ACK arriving after snapshot cannot remove a sale absent from snapshot totals.");
        Console.WriteLine("PASS shift-cache: exact per-order pending CASH delta and lost ACK deduplication");
    }

    private static async Task CurrentEndpointReconcilesAcknowledgedShifts()
    {
        await Reset();
        using (var seed = new LocalPosDbContext())
        {
            seed.CashDrawerShifts.Add(new LocalCashDrawerShift { ShiftId = 401, ClientShiftId = "stale", TerminalId = 1 });
            await seed.SaveChangesAsync();
        }
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Snapshot(402, "current")) }))) { BaseAddress = new Uri("http://test/") };
        await ShiftSession.CurrentAsync(http, 1, false);
        using var verify = new LocalPosDbContext();
        Check((await verify.CashDrawerShifts.FindAsync(401L))!.Status == "CLOSED", "Authoritative current endpoint retires stale acknowledged openings.");
        Console.WriteLine("PASS shift-cache: authoritative current reconciliation");
    }

    private static async Task ReferenceRefreshRetiresPlaceholder()
    {
        await Reset();
        using (var seed = new LocalPosDbContext())
        {
            seed.CashDrawerShifts.Add(new LocalCashDrawerShift { ShiftId = -20, ClientShiftId = "reference", BranchId = 1, TerminalId = 1, CashierId = "original-owner" });
            seed.Orders.Add(new LocalOrder { OrderId = "ref-pending", InvoiceNumber = "REF-1", BranchId = 1, TerminalId = 1, CashierId = "original-owner", PaymentStatus = "SUCCESS" });
            seed.Payments.Add(new LocalPayment { OrderId = "ref-pending", Amount = 25 });
            seed.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "Order", EntityId = "ref-pending", PayloadJson = JsonSerializer.Serialize(new OrderCreateDto { BranchId = 1, TerminalId = 1, ClientShiftId = "reference", ShiftId = -20 }) });
            await seed.SaveChangesAsync();
        }
        var dto = Snapshot(400, "reference", sales: 100); dto.AcknowledgedLocalTransactionIds = new();
        var data = new OfflineReferenceDataResponseDto { Version = "test", Terminal = new OfflineTerminalConfigDto { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalName = "test", BranchTaxType = "VAT", DeviceIdentifier = "test" }, ActiveShift = dto, Cashiers = new() { new() { UserId = 1, Username = "original-owner" } }, CashMovementCategories = new() { new() { CategoryId = 99, Name = "test", MovementType = "CASH_IN" } } };
        using var http = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(data) }))) { BaseAddress = new Uri("http://test/") };
        using var db = new LocalPosDbContext();
        Check(await new ReferenceDataSyncService(db).SyncReferenceDataAsync(1, http), "Reference refresh succeeds.");
        using var verify = new LocalPosDbContext();
        Check((await verify.CashDrawerShifts.FindAsync(-20L))!.Status == "CLOSED", "Reference insertion must retire matching negative placeholder.");
        Check((await verify.CashDrawerShifts.FindAsync(400L))!.TotalCashSales == 125, "Reference insertion uses exact pending CASH merge.");
        Check(await verify.TerminalConfigs.AnyAsync(t => t.TerminalId == 1), "Reference data inserts survive tracker management.");
        Console.WriteLine("PASS shift-cache: reference insert retires placeholder and merges pending sales");
    }

    private sealed class SnapshotTestDb(Microsoft.EntityFrameworkCore.DbContextOptions<POS.Api.Models.PosDbContext> options) : POS.Api.Models.PosDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entity in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                    if (property.GetDefaultValueSql()?.Contains("::", StringComparison.Ordinal) == true) property.SetDefaultValueSql(null);
        }
    }

    private static async Task ServerSnapshotIncludesExactIds()
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new SnapshotTestDb(new DbContextOptionsBuilder<POS.Api.Models.PosDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Branches.Add(new POS.Api.Models.Branch { BranchId = 1, Name = "test", TaxType = "VAT-Registered" });
        db.Outlets.Add(new POS.Api.Models.Outlet { OutletId = 1, BranchId = 1, OutletCode = "test", Name = "test" });
        db.Terminals.Add(new POS.Api.Models.Terminal { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalCode = "test", Name = "test" });
        db.CashDrawerShifts.AddRange(new POS.Api.Models.CashDrawerShift { ShiftId = 1, BranchId = 1, TerminalId = 1, CashierId = "owner", Status = "OPEN", TotalCashSales = 40 }, new POS.Api.Models.CashDrawerShift { ShiftId = 2, BranchId = 1, TerminalId = 1, CashierId = "owner", Status = "CLOSED" });
        db.Orders.AddRange(new POS.Api.Models.Order { OrderId = 1, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = 1, CashierId = "owner", OrderNumber = "a", OrderType = "RETAIL", OrderStatus = "COMPLETED", PaymentStatus = "PAID" }, new POS.Api.Models.Order { OrderId = 2, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = 2, CashierId = "owner", OrderNumber = "b", OrderType = "RETAIL", OrderStatus = "COMPLETED", PaymentStatus = "PAID" });
        db.OfflineSyncIdempotencyRecords.AddRange(new POS.Api.Models.OfflineSyncIdempotencyRecord { LocalTransactionId = "lost-ack", ServerOrderId = 1, InvoiceNumber = "a", PayloadHash = "a" }, new POS.Api.Models.OfflineSyncIdempotencyRecord { LocalTransactionId = "other-shift", ServerOrderId = 2, InvoiceNumber = "b", PayloadHash = "b" });
        await db.SaveChangesAsync();
        var snapshot = await POS.Api.Controllers.CashDrawerController.ReadSnapshotAsync(db, 1);
        Check(snapshot!.TotalCashSales == 40 && snapshot.AcknowledgedLocalTransactionIds!.SequenceEqual(new[] { "lost-ack" }), "Server snapshot returns only this shift's exact accepted IDs with totals.");
        Check(await POS.Api.Controllers.CashDrawerController.ReadSnapshotAsync(db, 2, requireOpen: true) == null, "Closed shift cannot be returned as current during reconciliation.");
        Console.WriteLine("PASS shift-cache: server exact-ID snapshot query executes against isolated SQLite");
    }

    private static async Task HistoricalAckDoesNotCloseNewerShift()
    {
        await Reset();
        using (var db = new LocalPosDbContext())
        {
            db.CashDrawerShifts.AddRange(
                new LocalCashDrawerShift { ShiftId = -1, ClientShiftId = "old", BranchId = 1, TerminalId = 1, CashierId = "original-owner" },
                new LocalCashDrawerShift { ShiftId = 200, ClientShiftId = "new", BranchId = 1, TerminalId = 1, CashierId = "original-owner" });
            await db.SaveChangesAsync();
        }
        await ShiftSession.CacheAsync(Snapshot(100, "old", "CLOSED"));
        using var verify = new LocalPosDbContext();
        Check((await verify.CashDrawerShifts.FindAsync(200L))!.Status == "OPEN", "Historical ACK must not close the newer drawer.");
        Check((await verify.CashDrawerShifts.FindAsync(-1L))!.Status == "CLOSED", "ACK retires its matching placeholder.");
        Console.WriteLine("PASS shift-cache: historical ACK preserves newer drawer");
    }
}
