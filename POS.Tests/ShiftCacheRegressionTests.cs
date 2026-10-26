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
        await OfflineShiftCloseAndSync();
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

    private static async Task OfflineShiftCloseAndSync()
    {
        await Reset();
        using (var seed = new LocalPosDbContext())
        {
            seed.TerminalConfigs.Add(new LocalTerminalConfig { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalName = "T1", TerminalIdentifier = "T1", BranchTaxType = "VAT" });
            await seed.SaveChangesAsync();
        }

        using var offlineHttp = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not call network offline.")));
        var openReq = new OpenShiftDto { BranchId = 1, TerminalId = 1, OpeningFloat = 500m, ClientShiftId = "off-shift-1", OpenedAt = DateTime.UtcNow.AddHours(-2) };
        var localShift = await ShiftSession.OpenAsync(offlineHttp, openReq, "original-owner", offline: true);

        using (var saleDb = new LocalPosDbContext())
        {
            var tracked = await saleDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == localShift.ShiftId);
            tracked.TotalCashSales = 125.25m;
            tracked.ExpectedBalance = 625.25m;
            saleDb.Orders.Add(new LocalOrder { OrderId = "off-ord-1", InvoiceNumber = "INV-OFF-1", BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = localShift.ShiftId, ClientShiftId = localShift.ClientShiftId, CashierId = "original-owner", Subtotal = 200m, TotalAmount = 200m, PaymentStatus = "SUCCESS", OrderStatus = "COMPLETED", CreatedAt = DateTime.UtcNow.AddHours(-1) });
            saleDb.Payments.Add(new LocalPayment { OrderId = "off-ord-1", Amount = 200m, PaymentMethod = "GCASH", PaymentStatus = "SUCCESS", PaidAt = DateTime.UtcNow.AddHours(-1) });
            await saleDb.SaveChangesAsync();
        }

        // 1. Offline structured close without IsDeclarationConfirmed = true must be rejected
        bool rejectedWithoutConfirm = false;
        try
        {
            await ShiftSession.CloseAsync(offlineHttp, new CloseShiftDto
            {
                ShiftId = localShift.ShiftId,
                ClientShiftId = localShift.ClientShiftId,
                IsDeclarationConfirmed = false,
                Denominations = [new() { Type = "BILL", Denomination = 500m, Quantity = 1 }],
                PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 200m }]
            }, "original-owner", offline: true);
        }
        catch (InvalidOperationException)
        {
            rejectedWithoutConfirm = true;
        }
        Check(rejectedWithoutConfirm, "Offline blind close without IsDeclarationConfirmed=true must be rejected.");

        // 2. Offline blind close with exact centavo denominations succeeds without discrepancy remarks,
        //    returns blind-redacted DTO to cashier, and queues SHIFT_CLOSE
        var closeReq = new CloseShiftDto
        {
            ShiftId = localShift.ShiftId,
            ClientShiftId = localShift.ClientShiftId,
            ClientCloseId = "off-close-1",
            IsDeclarationConfirmed = true,
            Denominations =
            [
                new() { Type = "BILL", Denomination = 500m, Quantity = 1 },
                new() { Type = "BILL", Denomination = 100m, Quantity = 1 },
                new() { Type = "COIN", Denomination = 20m, Quantity = 1 },
                new() { Type = "COIN", Denomination = 5m, Quantity = 1 },
                new() { Type = "COIN", Denomination = 0.25m, Quantity = 1 }
            ],
            PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 190m }], // -10 short on GCash
            Notes = "" // No discrepancy remarks required for blind close
        };

        var closedDto = await ShiftSession.CloseAsync(offlineHttp, closeReq, "original-owner", offline: true);
        Check(closedDto.Status == "CLOSED" && closedDto.ActualCountedBalance == 625.25m, "Offline close must compute exact centavo balance.");
        Check(closedDto.IsBlindCloseRedacted && closedDto.Discrepancy == null && closedDto.XReading == null && closedDto.CashierDeclaration != null,
            "Offline cashier close response must be blind-redacted with CashierDeclaration acknowledgment.");

        // 3. Reprint from saved local closing record: cashier receives CashierDeclaration reprint, supervisor receives full XReading
        var lastClosedCashier = await ShiftSession.LastClosedDtoAsync(offlineHttp, 1, offline: true, isSupervisor: false);
        Check(lastClosedCashier != null && lastClosedCashier.IsBlindCloseRedacted && lastClosedCashier.CashierDeclaration?.IsReprint == true && lastClosedCashier.XReading == null,
            "LastClosedDtoAsync for cashier must return blind-redacted record with CashierDeclaration IsReprint=true.");

        var lastClosedSupervisor = await ShiftSession.LastClosedDtoAsync(offlineHttp, 1, offline: true, isSupervisor: true);
        Check(lastClosedSupervisor != null && !lastClosedSupervisor.IsBlindCloseRedacted && lastClosedSupervisor.Discrepancy == 0m && lastClosedSupervisor.XReading?.OverallStatus == "SHORT" && lastClosedSupervisor.XReading?.IsReprint == true,
            "LastClosedDtoAsync for supervisor must return full reconciliation and XReading with SHORT status.");

        // 4. Offline supervisor recount preserves OriginalDeclaration and appends RecountHistory
        var recountedOffline = await ShiftSession.RecountAsync(offlineHttp, localShift.ShiftId, new ShiftRecountRequestDto
        {
            ShiftId = localShift.ShiftId,
            Reason = "Offline manager recount found ₱10 GCash slip",
            Denominations = closeReq.Denominations,
            PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 200m }]
        }, "manager1", "Manager", offline: true);
        Check(recountedOffline.OriginalDeclaration?.TotalNonCashDeclared == 190m && recountedOffline.TotalNonCashDeclared == 200m && recountedOffline.RecountHistory.Count == 1,
            "Offline supervisor recount must preserve OriginalDeclaration (190) and update TotalNonCashDeclared (200) with RecountHistory.");

        // 5. Synchronizing SHIFT_CLOSE outbox automatically uploads prerequisite SHIFT_OPEN and marks both SYNCED
        int openCalls = 0, closeCalls = 0;
        using var syncHttp = new HttpClient(new Handler(async req =>
        {
            if (req.RequestUri!.AbsolutePath.EndsWith("open-shift"))
            {
                openCalls++;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new CashDrawerShiftDto
                    {
                        ShiftId = 555,
                        ClientShiftId = "off-shift-1",
                        BranchId = 1,
                        TerminalId = 1,
                        CashierId = "original-owner",
                        OpeningFloat = 500m,
                        OpeningConfirmed = true,
                        Status = "OPEN",
                        OpenedAt = openReq.OpenedAt!.Value
                    })
                };
            }
            if (req.RequestUri.AbsolutePath.EndsWith("close-shift"))
            {
                closeCalls++;
                var sentClose = await req.Content!.ReadFromJsonAsync<CloseShiftDto>();
                Check(sentClose!.ShiftId == 555 && sentClose.ClientCloseId == "off-close-1" && sentClose.IsDeclarationConfirmed && sentClose.ActualCountedBalance == 625.25m,
                    "Uploaded SHIFT_CLOSE must resolve server ShiftId 555 and preserve counted balance and IsDeclarationConfirmed.");
                var ackDto = Snapshot(555, "off-shift-1", "CLOSED", sales: 125.25m);
                ackDto.ClientCloseId = "off-close-1";
                ackDto.ActualCountedBalance = 625.25m;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ackDto) };
            }
            throw new InvalidOperationException("Unexpected endpoint: " + req.RequestUri);
        })) { BaseAddress = new Uri("http://test/") };

        using (var syncDb = new LocalPosDbContext())
        {
            var closeOutbox = await syncDb.SyncOutboxes.SingleAsync(o => o.EntityType == "SHIFT_CLOSE");
            var svc = new OfflineTransactionSyncService(syncDb);
            Check(await svc.UploadTransactionAsync(closeOutbox, syncHttp), "SHIFT_CLOSE outbox upload must succeed.");
        }
        Check(openCalls == 1 && closeCalls == 1, "SHIFT_CLOSE sync must upload prerequisite SHIFT_OPEN first and then close-shift.");
        Console.WriteLine("PASS shift-cache: offline shift close, reprint from saved record, and SHIFT_CLOSE outbox sync");
    }
}
