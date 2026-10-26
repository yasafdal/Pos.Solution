using System.Data.Common;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Client.Tests;
internal static class ApiAndSchemaTests
{
    private const string MigrationId = "20261026035721_ConfirmedOpeningFloat";
    private static string? postgres;
    internal static async Task RunAll(Func<string, Func<Task>, Task> run)
    {
        await run("EF snapshot has no model drift; downgrade refuses audit destruction", Snapshot);
        postgres = Environment.GetEnvironmentVariable("POS_TEST_POSTGRES_CONNECTION");
        if (postgres == null) { Console.WriteLine("SKIP PostgreSQL: set POS_TEST_POSTGRES_CONNECTION to a DISPOSABLE loopback database pos_opening_test_*."); return; }
        var cs = new NpgsqlConnectionStringBuilder(postgres);
        Program.Check(cs.Host is "127.0.0.1" or "localhost" && cs.Database?.StartsWith("pos_opening_test_", StringComparison.Ordinal) == true, "Refusing non-disposable PostgreSQL target.");
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await run("PostgreSQL idempotent Up, manual compatibility, defaults/indexes/readiness", Schema);
        await run("API SHIFT_OPEN rejects wrong original cashier and terminal", ApiOwnership);
        await run("PostgreSQL offline opening, historical sale replay and retry", OpeningReplay);
        await run("PostgreSQL concurrent opening retries are idempotent", () => OpeningRace(true));
        await run("PostgreSQL concurrent distinct openings cannot duplicate drawer", () => OpeningRace(false));
        foreach (var method in new[] { "CASH", "CARD" })
        {
            await run($"PostgreSQL real two-context close first vs {method}", () => Race(true, method));
            await run($"PostgreSQL real two-context {method} first vs close", () => Race(false, method));
        }
    }
    private static PosDbContext Context(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql(postgres ?? "Host=127.0.0.1;Database=pos_schema_design_only;Username=not_configured");
        if (interceptors.Length > 0) options.AddInterceptors(interceptors);
        return new PosDbContext(options.Options);
    }
    private static Task Snapshot()
    {
        using var db = Context(); var migrator = db.GetService<IMigrator>();
        Program.Check(!db.Database.HasPendingModelChanges(), "Snapshot drift detected.");
        var sql = migrator.GenerateScript("20261021180419_OrderServiceModeAndTable", MigrationId, MigrationsSqlGenerationOptions.Idempotent);
        Program.Check(sql.Contains("ADD COLUMN IF NOT EXISTS") && sql.Contains("CREATE UNIQUE INDEX IF NOT EXISTS") && !sql.Contains("DROP "), "Migration not additive/idempotent.");
        try { migrator.GenerateScript(MigrationId, "20261021180419_OrderServiceModeAndTable"); }
        catch (NotSupportedException) { return Task.CompletedTask; }
        throw new Exception("Down must refuse audit destruction.");
    }
    private static string RepoFile(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "POS.Api", "POS.Api.csproj"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new Exception("Repository not located."), relative);
    }
    private static async Task ResetMinimal()
    {
        using var db = Context();
        await db.Database.ExecuteSqlRawAsync("""
            DROP SCHEMA public CASCADE; CREATE SCHEMA public;
            CREATE TABLE "CashDrawerShifts" ("ShiftId" bigint PRIMARY KEY, "TerminalId" integer NOT NULL, "Status" varchar(30) NOT NULL, "OpeningFloat" numeric(18,2) NOT NULL);
            INSERT INTO "CashDrawerShifts" VALUES (1,1,'OPEN',0), (2,2,'CLOSED',75);
            """);
    }
    private static async Task Schema()
    {
        await ResetMinimal(); using var db = Context();
        var migration = new POS.Api.Migrations.ConfirmedOpeningFloat();
        var raw = string.Join("\n", db.GetService<IMigrationsSqlGenerator>().Generate(migration.UpOperations, db.Model).Select(c => c.CommandText));
        await db.Database.ExecuteSqlRawAsync(raw); await db.Database.ExecuteSqlRawAsync(raw);
        Program.Check(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"CashDrawerShifts\" WHERE \"OpeningConfirmed\" = false AND \"ClientShiftId\" IS NULL").SingleAsync() == 2, "Legacy float was marked confirmed.");
        var script = File.ReadAllText(RepoFile("POS.Api/Sql/confirmed-opening-float.ef.sql"));
        await db.Database.ExecuteSqlRawAsync(script); await db.Database.ExecuteSqlRawAsync(script);
        await db.Database.ExecuteSqlRawAsync(File.ReadAllText(RepoFile("POS.Api/Sql/confirmed-opening-float.sql")));
        Program.Check(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261026035721_ConfirmedOpeningFloat'").SingleAsync() == 1, "History row count incorrect.");
        Program.Check(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE schemaname='public' AND indexname IN ('UX_CashDrawerShifts_OpenTerminal','UX_CashDrawerShifts_ClientShiftId') AND indexdef LIKE '%UNIQUE%'").SingleAsync() == 2, "Unique indexes missing.");
        await ExpectPostgres("23505", () => db.Database.ExecuteSqlRawAsync("INSERT INTO \"CashDrawerShifts\" (\"ShiftId\",\"TerminalId\",\"Status\",\"OpeningFloat\") VALUES (3,1,'OPEN',0)"));
        await db.Database.ExecuteSqlRawAsync("UPDATE \"CashDrawerShifts\" SET \"ClientShiftId\"='same' WHERE \"ShiftId\"=1");
        await ExpectPostgres("23505", () => db.Database.ExecuteSqlRawAsync("UPDATE \"CashDrawerShifts\" SET \"ClientShiftId\"='same' WHERE \"ShiftId\"=2"));
        await ResetMinimal(); await db.Database.ExecuteSqlRawAsync("INSERT INTO \"CashDrawerShifts\" VALUES (3,1,'OPEN',0)");
        await ExpectPostgres("P0001", () => db.Database.ExecuteSqlRawAsync(raw));
        Program.Check(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"CashDrawerShifts\"").SingleAsync() == 3, "Readiness failure discarded shifts.");
        await ResetMinimal();
        await db.Database.ExecuteSqlRawAsync(File.ReadAllText(RepoFile("POS.Api/Sql/confirmed-opening-float.sql")));
        await db.Database.ExecuteSqlRawAsync(script); await db.Database.ExecuteSqlRawAsync(script);
        Program.Check(await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = '20261026035721_ConfirmedOpeningFloat'").SingleAsync() == 1, "Manual-first deployment not adopted exactly once.");
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"UX_CashDrawerShifts_ClientShiftId\"; UPDATE \"CashDrawerShifts\" SET \"ClientShiftId\"='duplicate'");
        await ExpectPostgres("P0001", () => db.Database.ExecuteSqlRawAsync(raw));
    }
    private static async Task ExpectPostgres(string state, Func<Task> action)
    {
        try { await action(); } catch (PostgresException e) when (e.SqlState == state) { return; }
        throw new Exception("Expected PostgreSQL " + state);
    }
    private static async Task<long> Seed()
    {
        using var db = Context(); await db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;"); await db.Database.EnsureCreatedAsync();
        db.Branches.Add(new Branch { BranchId = 1, Name = "Scratch", TaxType = "VAT-Registered", IsActive = true });
        db.Outlets.Add(new Outlet { OutletId = 1, BranchId = 1, OutletCode = "T", Name = "Scratch", IsActive = true });
        db.Terminals.Add(new Terminal { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalCode = "T1", Name = "Scratch", IsActive = true });
        db.CashDrawerShifts.Add(new CashDrawerShift { BranchId = 1, TerminalId = 1, CashierId = "cashier1", OpeningFloat = 100, ExpectedBalance = 100, OpeningConfirmed = true, ClientShiftId = "pg-test", OpenedAt = DateTime.UtcNow.AddHours(-1) });
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "PG", Prefix = "PG-", StartingNumber = 1, EndingNumber = 100, LastIssuedNumber = 0, IsActive = true });
        var order = new Order { BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", OrderNumber = "PG-ORDER", OrderType = "RETAIL", PaymentStatus = "UNPAID", OrderStatus = "PENDING", TotalAmount = 25 };
        db.Orders.Add(order); await db.SaveChangesAsync(); return order.OrderId;
    }
    private static T Authenticate<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "cashier1"), new Claim(ClaimTypes.Role, "Cashier"), new Claim("branch_id", "1"), new Claim("terminal_id", "1")], "test")) } }; return controller;
    }
    private static async Task ApiOwnership()
    {
        await Seed(); using var db = Context(); var api = Authenticate(new CashDrawerController(db));
        var request = new OpenShiftDto { BranchId = 1, TerminalId = 1, OpeningFloat = 10, CashierId = "cashier2", ClientShiftId = "wrong-owner" };
        Program.Check(await api.OpenShift(request) is ForbidResult, "Upload transferred original cashier to uploader.");
        request.CashierId = "cashier1"; request.TerminalId = 2;
        Program.Check(await api.OpenShift(request) is ForbidResult, "Upload escaped assigned terminal.");
        Program.Check(await db.CashDrawerShifts.CountAsync() == 1 && (await db.CashDrawerShifts.SingleAsync()).OpeningFloat == 100, "Rejected upload mutated drawer.");
    }
    private static async Task OpeningReplay()
    {
        await Seed();
        using (var db = Context())
        {
            var original = await db.CashDrawerShifts.SingleAsync();
            db.CashDrawerShifts.Remove(original); await db.SaveChangesAsync();
        }
        var opened = DateTime.UtcNow.AddMinutes(-5);
        var opening = new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier1", OpeningFloat = 100, ClientShiftId = "offline-replay", OpenedAt = opened };
        long shiftId;
        using (var db = Context())
        {
            var result = await Authenticate(new CashDrawerController(db)).OpenShift(opening);
            Program.Check(result is OkObjectResult, "Offline opening failed: " + Describe(result));
            shiftId = (await db.CashDrawerShifts.SingleAsync()).ShiftId;
        }
        using (var db = Context())
        {
            var api = Authenticate(new CashDrawerController(db));
            Program.Check(await api.OpenShift(opening) is OkObjectResult && await db.CashDrawerShifts.CountAsync() == 1, "Opening ACK retry created duplicate or failed timestamp precision.");
            Program.Check(await api.CloseShift(new CloseShiftDto { ShiftId = shiftId, ActualCountedBalance = 125 }) is OkObjectResult, "Close failed.");
        }
        var replay = new OfflineTransactionSyncRequest
        {
            LocalTransactionId = "pg-offline-sale", InvoiceNumber = "PG-000001", TerminalId = 1, BranchId = 1,
            CashierId = "cashier1", ClientShiftId = "offline-replay", TransactionDate = opened.AddMinutes(1),
            PayloadHash = "pg-offline-hash", Subtotal = 25, TotalAmount = 25,
            Payments = [new OfflinePaymentDto { PaymentMethod = "CASH", Amount = 25, TenderedAmount = 30, ChangeAmount = 5, PaymentStatus = "SUCCESS", PaidAt = opened.AddMinutes(1) }]
        };
        for (var i = 0; i < 2; i++)
        {
            using var db = Context();
            var result = await Authenticate(new OfflineSyncController(db)).SyncTransaction(replay);
            Program.Check(result.Result is OkObjectResult, "Historical replay failed: " + Describe(result.Result!));
        }
        using (var db = Context())
        {
            var shift = await db.CashDrawerShifts.SingleAsync();
            Program.Check(shift.Status == "CLOSED" && shift.TotalCashSales == 25 && shift.ExpectedBalance == 125 && shift.Discrepancy == 0, "Historical replay altered wrong lifecycle or double-counted float/sales.");
            Program.Check(await db.Payments.CountAsync() == 1 && await db.OfflineSyncIdempotencyRecords.CountAsync() == 1, "Replay retry duplicated payment.");
            var snapshot = await CashDrawerController.ReadSnapshotAsync(db, shiftId);
            Program.Check(snapshot?.AcknowledgedLocalTransactionIds?.SequenceEqual(["pg-offline-sale"]) == true, "PostgreSQL snapshot omitted exact acknowledged transaction.");
        }
    }
    private sealed class LockGate(bool hold) : DbCommandInterceptor
    {
        public readonly TaskCompletionSource Executing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Acquired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static bool IsLock(DbCommand c) => c.CommandText.Contains("FOR UPDATE") && c.CommandText.Contains("Terminals");
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (IsLock(command)) Executing.TrySetResult(); return ValueTask.FromResult(result); }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (IsLock(command)) { Acquired.TrySetResult(); if (hold) await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); } return result;
        }
    }
    private static async Task OpeningRace(bool sameIdentity)
    {
        await Seed();
        using (var db = Context()) { db.CashDrawerShifts.RemoveRange(await db.CashDrawerShifts.ToListAsync()); await db.SaveChangesAsync(); }
        var first = new LockGate(true); var second = new LockGate(false);
        using var firstDb = Context(first); using var secondDb = Context(second);
        var opened = DateTime.UtcNow;
        OpenShiftDto Request(string identity) => new() { TerminalId = 1, BranchId = 1, OpeningFloat = 0, CashierId = "cashier1", ClientShiftId = identity, OpenedAt = opened };
        var task1 = Authenticate(new CashDrawerController(firstDb)).OpenShift(Request("concurrent-open"));
        await first.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var task2 = Authenticate(new CashDrawerController(secondDb)).OpenShift(Request(sameIdentity ? "concurrent-open" : "competing-open"));
        try
        {
            await second.Executing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(100);
            Program.Check(!task2.IsCompleted, "Opening must wait for terminal lock.");
        }
        finally { first.Release.TrySetResult(); }
        Program.Check(await task1 is OkObjectResult, "First opening failed.");
        var secondResult = await task2;
        Program.Check(sameIdentity ? secondResult is OkObjectResult : secondResult is ConflictObjectResult, "Concurrent opening result violated idempotency: " + Describe(secondResult));
        using var verify = Context();
        Program.Check(await verify.CashDrawerShifts.CountAsync() == 1 && (await verify.CashDrawerShifts.SingleAsync()).OpeningFloat == 0, "Concurrent request duplicated float or drawer.");
    }
    private static async Task Race(bool closeFirst, string method)
    {
        var orderId = await Seed(); var first = new LockGate(true); var second = new LockGate(false);
        using var firstDb = Context(first); using var secondDb = Context(second);
        long shiftId; using (var db = Context()) shiftId = (await db.CashDrawerShifts.SingleAsync()).ShiftId;
        async Task<IActionResult> Close(PosDbContext db) => await Authenticate(new CashDrawerController(db)).CloseShift(new CloseShiftDto { ShiftId = shiftId, ActualCountedBalance = method == "CASH" && !closeFirst ? 125 : 100 });
        async Task<IActionResult> Pay(PosDbContext db) => (await Authenticate(new PaymentsController(db, new SequenceService(db))).ProcessPayment(new PaymentProcessDto { OrderId = orderId, AmountTendered = 25, PaymentMethod = method, ShiftId = shiftId })).Result!;
        var task1 = closeFirst ? Close(firstDb) : Pay(firstDb); Task<IActionResult>? task2 = null;
        try
        {
            var reached = await Task.WhenAny(first.Acquired.Task, task1, Task.Delay(15000));
            Program.Check(reached == first.Acquired.Task, task1.IsCompleted ? "Operation returned before lock: " + Describe(await task1) : "Lock interceptor not reached.");
            task2 = closeFirst ? Pay(secondDb) : Close(secondDb);
            await second.Executing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            using var monitor = Context(); var deadline = DateTime.UtcNow.AddSeconds(10); bool blocked = false;
            while (DateTime.UtcNow < deadline)
            {
                blocked = await monitor.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type='Lock' AND query LIKE '%Terminals%FOR UPDATE%'").SingleAsync() > 0;
                if (blocked) break; await Task.Delay(25);
            }
            Program.Check(blocked && !task2.IsCompleted, "Second context did not actually block on PostgreSQL terminal lock.");
        }
        finally { first.Release.TrySetResult(); }
        var result1 = await task1.WaitAsync(TimeSpan.FromSeconds(20)); var result2 = await task2!.WaitAsync(TimeSpan.FromSeconds(20));
        Program.Check(result1 is OkObjectResult, "First operation failed: " + Describe(result1));
        Program.Check(closeFirst ? result2 is ConflictObjectResult : result2 is OkObjectResult, "Waiting operation incorrect: " + Describe(result2));
        using var verify = Context(); var shift = await verify.CashDrawerShifts.SingleAsync(); var order = await verify.Orders.SingleAsync();
        Program.Check(shift.Status == "CLOSED" && await verify.Payments.CountAsync() == (closeFirst ? 0 : 1), "Shift/payment state inconsistent.");
        var cash = !closeFirst && method == "CASH" ? 25 : 0;
        Program.Check(shift.TotalCashSales == cash && shift.ExpectedBalance == 100 + cash && shift.Discrepancy == 0, "Close lost or misclassified cash revenue.");
        Program.Check(order.PaymentStatus == (closeFirst ? "UNPAID" : "PAID") && (await verify.InvoiceSequences.SingleAsync()).LastIssuedNumber == (closeFirst ? 0 : 1), "Order/invoice inconsistent.");
        Console.WriteLine($"Observed PostgreSQL Lock wait; {method}, closeFirst={closeFirst}; payments={(closeFirst ? 0 : 1)}, expected={shift.ExpectedBalance}, discrepancy={shift.Discrepancy}.");
    }
    private static string Describe(IActionResult result) => result is ObjectResult o ? $"{o.StatusCode}: {o.Value}" : result.GetType().Name;
}
