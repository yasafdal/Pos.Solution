using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Client.Tests;
internal static class Program
{
    private static readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "hermes", "cache", "scratch", "opening-float-tests-" + Guid.NewGuid().ToString("N"));
    private static int failed;
    private static int passed;
    private static string DbPath = "";
    private static readonly HttpClient OfflineHttp = new(new RejectNetwork());
    private sealed class RejectNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException("Offline tests must not use network.");
    }
    public static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Root);
        if (args.Length > 0 && args[0] == "--restart-child")
        {
            DbPath = args[1]; AppContext.SetData("POS.LocalDatabasePath", DbPath);
            GuardPath();
            var shift = await ShiftSession.CurrentAsync(OfflineHttp, 1, true);
            Check(shift != null && shift.OpeningConfirmed && shift.TotalCashSales == 25m && shift.ExpectedBalance == 125m, "Fresh process did not recover persisted opening and sale.");
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            Check(await db.SyncOutboxes.CountAsync() == 2, "Fresh process lost opening/order outboxes.");
            Console.WriteLine("Fresh process recovered SQLite opening, sale and outboxes."); return 0;
        }
        await Run("Scratch path seam cannot access user SQLite", () => { NewPath(); GuardPath(); return Task.CompletedTask; });
        if (failed > 0) return 1; // Never initialize the default user database if the seam is absent.
        await Run("Real local opening, offline checkout, retry and process restart", LocalLifecycle);
        await Run("Blank/negative opening and wrong terminal/branch are rejected", InvalidOpenings);
        await Run("Checkout cannot bypass authenticated cashier/terminal", WrongSession);
        await Run("Checkout without confirmed float leaves no sale or sequence mutation", Unconfirmed);
        await Run("Historical closed ACK does not close newer open shift", HistoricalAck);
        await Run("Cache regression: replay owner, exact pending cash and placeholder reconciliation", async () =>
        {
            NewPath(); GuardPath(); Environment.SetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH", DbPath);
            try { await POS.Tests.ShiftCacheRegressionTests.RunAsync(); }
            finally { Environment.SetEnvironmentVariable("POS_TEST_LOCAL_DB_PATH", null); }
        });
        await ApiAndSchemaTests.RunAll(Run);
        Console.WriteLine($"RESULT: {passed} passed, {failed} failed");
        SqliteConnection.ClearAllPools(); AppContext.SetData("POS.LocalDatabasePath", null);
        try { Directory.Delete(Root, true); } catch (IOException) { }
        return failed == 0 ? 0 : 1;
    }
    internal static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Run(string name, Func<Task> action)
    {
        try { await action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    }
    private static void NewPath()
    {
        DbPath = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
        AppContext.SetData("POS.LocalDatabasePath", DbPath);
    }
    private static void GuardPath()
    {
        using var db = new LocalPosDbContext();
        Check(Path.GetFullPath(db.Database.GetDbConnection().DataSource) == Path.GetFullPath(DbPath), "Local database path seam missing; refusing any SQLite writes.");
    }
    private static async Task Seed()
    {
        NewPath(); GuardPath(); using var db = new LocalPosDbContext(); db.InitializeDatabase();
        db.TerminalConfigs.Add(new LocalTerminalConfig { TerminalId = 1, BranchId = 1, OutletId = 1, TerminalName = "Test", TerminalIdentifier = "TEST", BranchTaxType = "Non-VAT" });
        db.InvoiceSequences.Add(new LocalInvoiceSequence { TerminalId = 1, SeriesCode = "TEST", Prefix = "TEST-", StartingNumber = 1, EndingNumber = 100, LastIssuedNumber = 0 });
        db.Products.Add(new LocalProduct { ProductId = 1, Name = "Item", Price = 25, IsActive = true });
        await db.SaveChangesAsync();
    }
    private static OpenShiftDto Opening(decimal? amount = 100, int terminal = 1, int branch = 1) => new() { TerminalId = terminal, BranchId = branch, OpeningFloat = amount, ClientShiftId = Guid.NewGuid().ToString("N"), OpenedAt = DateTime.UtcNow };
    private static OrderCreateDto Sale() => new() { TerminalId = 1, BranchId = 1, OutletId = 1, CashierId = "cashier1", Items = [new() { ProductId = 1, ProductName = "Item", Quantity = 1, BasePrice = 25, TaxName = "VATExempt" }] };
    private static PosSessionService Session(string cashier = "cashier1", int terminal = 1, int branch = 1)
    {
        var session = new PosSessionService(); session.SetSession("test-token", cashier, "Cashier", new TerminalConfigDto { TerminalId = terminal, BranchId = branch, OutletId = 1 }); return session;
    }
    private static async Task<OfflineCheckoutResultDto> Checkout(PosSessionService session, OrderCreateDto? sale = null, string cashier = "cashier1")
    {
        using var db = new LocalPosDbContext(); return await new OfflineCheckoutService(db, session).ProcessOfflineCashSaleAsync(sale ?? Sale(), 30, cashier);
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected opening rejection.");
    }
    private static async Task LocalLifecycle()
    {
        await Seed(); var request = Opening(); var shift = await ShiftSession.OpenAsync(OfflineHttp, request, "cashier1", true);
        Check(shift.ShiftId < 0 && shift.OpeningConfirmed && shift.ExpectedBalance == 100 && shift.TotalCashSales == 0, "Opening float incorrectly counted as revenue.");
        await Reject(() => ShiftSession.OpenAsync(OfflineHttp, request, "cashier1", true));
        var sale = await Checkout(Session()); Check(sale.Success && sale.ChangeAmount == 5, sale.ErrorMessage ?? "Checkout failed.");
        using (var db = new LocalPosDbContext())
        {
            var saved = await db.CashDrawerShifts.SingleAsync(); Check(saved.TotalCashSales == 25 && saved.ExpectedBalance == 125, "Float/sales totals wrong.");
            Check(await db.Orders.CountAsync() == 1 && await db.Payments.CountAsync() == 1 && (await db.InvoiceSequences.SingleAsync()).LastIssuedNumber == 1, "Atomic sale state wrong.");
            var opening = JsonSerializer.Deserialize<OpenShiftDto>((await db.SyncOutboxes.SingleAsync(o => o.EntityType == "SHIFT_OPEN")).PayloadJson)!;
            Check(opening.CashierId == "cashier1" && opening.ClientShiftId == request.ClientShiftId, "Opening outbox lost owner or identity.");
            var payload = JsonSerializer.Deserialize<OrderCreateDto>((await db.SyncOutboxes.SingleAsync(o => o.EntityType == "Order")).PayloadJson)!;
            Check(payload.ClientShiftId == shift.ClientShiftId && payload.ShiftId == shift.ShiftId, "Sale lost original opening linkage.");
        }
        SqliteConnection.ClearAllPools();
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add("--restart-child"); start.ArgumentList.Add(DbPath);
        using var process = System.Diagnostics.Process.Start(start)!; var output = await process.StandardOutput.ReadToEndAsync(); var error = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "Restart child failed: " + output + error); Console.Write(output);
        using var reopened = new LocalPosDbContext(); reopened.InitializeDatabase(); Check(await reopened.CashDrawerShifts.CountAsync() == 1, "Repeated initialization duplicated shift.");
    }
    private static async Task InvalidOpenings()
    {
        await Seed(); foreach (var amount in new decimal?[] { null, -1 }) await Reject(() => ShiftSession.OpenAsync(OfflineHttp, Opening(amount), "cashier1", true));
        await Reject(() => ShiftSession.OpenAsync(OfflineHttp, Opening(0, 2), "cashier1", true));
        await Reject(() => ShiftSession.OpenAsync(OfflineHttp, Opening(0, 1, 2), "cashier1", true));
        var zero = await ShiftSession.OpenAsync(OfflineHttp, Opening(0), "cashier1", true); Check(zero.OpeningConfirmed && zero.OpeningFloat == 0, "Explicit zero must be accepted.");
    }
    private static async Task WrongSession()
    {
        await Seed(); await ShiftSession.OpenAsync(OfflineHttp, Opening(), "cashier1", true);
        foreach (var session in new[] { new PosSessionService(), Session("cashier2"), Session(terminal: 2), Session(branch: 2) })
            Check(!(await Checkout(session)).Success, "Authenticated owner/terminal bypass accepted.");
        var wrongPayload = Sale(); wrongPayload.CashierId = "cashier2"; Check(!(await Checkout(Session(), wrongPayload)).Success, "Payload owner bypass accepted.");
        Check(!(await Checkout(Session(), cashier: "cashier2")).Success, "Parameter owner bypass accepted.");
        var wrongTerminal = Sale(); wrongTerminal.TerminalId = 2; Check(!(await Checkout(Session(), wrongTerminal)).Success, "Wrong requested terminal accepted.");
        using var db = new LocalPosDbContext(); Check(!await db.Orders.AnyAsync() && !await db.Payments.AnyAsync() && (await db.InvoiceSequences.SingleAsync()).LastIssuedNumber == 0 && (await db.CashDrawerShifts.SingleAsync()).TotalCashSales == 0, "Rejected checkout mutated financial state.");
    }
    private static async Task Unconfirmed()
    {
        await Seed(); Check(!(await Checkout(Session())).Success, "Checkout without opening accepted.");
        await ShiftSession.OpenAsync(OfflineHttp, Opening(), "cashier1", true);
        using (var db = new LocalPosDbContext()) { (await db.CashDrawerShifts.SingleAsync()).OpeningConfirmed = false; await db.SaveChangesAsync(); }
        Check(!(await Checkout(Session())).Success, "Legacy unconfirmed opening accepted.");
        using var verify = new LocalPosDbContext(); Check(!await verify.Orders.AnyAsync() && (await verify.InvoiceSequences.SingleAsync()).LastIssuedNumber == 0, "Rejection was not atomic.");
    }
    private static async Task HistoricalAck()
    {
        await Seed(); await ShiftSession.CacheAsync(new CashDrawerShiftDto { ShiftId = 200, TerminalId = 1, BranchId = 1, CashierId = "cashier1", OpeningConfirmed = true, OpeningFloat = 100, Status = "OPEN", ClientShiftId = "new", OpenedAt = DateTime.UtcNow });
        await ShiftSession.CacheAsync(new CashDrawerShiftDto { ShiftId = 100, TerminalId = 1, BranchId = 1, CashierId = "cashier1", OpeningConfirmed = true, OpeningFloat = 50, Status = "CLOSED", ClientShiftId = "old", OpenedAt = DateTime.UtcNow.AddHours(-2), ClosedAt = DateTime.UtcNow.AddHours(-1) });
        var current = await ShiftSession.CurrentAsync(OfflineHttp, 1, true); Check(current?.ShiftId == 200, "Historical acknowledgement closed the newer drawer.");
    }
}
