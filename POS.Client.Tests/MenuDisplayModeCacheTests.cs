using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Client.Tests;

internal static class MenuDisplayModeCacheTests
{
    private static readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "hermes", "cache", "scratch", "menu-cache-tests-" + Guid.NewGuid().ToString("N"));

    internal static async Task RunAll(Func<string, Func<Task>, Task> run)
    {
        Directory.CreateDirectory(Root);
        try
        {
            await run("Menu mode: successful reference sync persists ImageAndText without extra HTTP", SuccessfulSync);
            await run("Menu mode: reference refresh preserves local printer assignments", PrinterPreservation);
            await run("Menu mode: session reload restores persisted mode and normalizes unknown/missing config", SessionReload);
            await run("Menu mode: login and lock paths propagate mode before navigation", LoginWiring);
            await run("Menu mode: additive old-schema upgrade preserves config and fiscal rows", OldSchema);
            await run("Menu mode: failed HTTP, malformed/invalid payload and rollback retain committed cache", FailedSyncs);
            await run("Menu mode: text/unknown refreshes default to text without image downloads", TextAndUnknown);
            await run("Device activation: generates client UUID, posts code, saves BIR details and marks SQLite provisioned", ClientDeviceActivation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", null);
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    private static LocalPosDbContext NewDatabase(bool initialize = true)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
        AppContext.SetData("POS.LocalDatabasePath", path);
        var db = new LocalPosDbContext();
        Program.Check(Path.GetFullPath(db.Database.GetDbConnection().DataSource) == Path.GetFullPath(path), "Refusing writes outside isolated SQLite.");
        if (initialize) db.InitializeDatabase();
        return db;
    }

    private static OfflineReferenceDataResponseDto Payload(int mode) => JsonSerializer.Deserialize<OfflineReferenceDataResponseDto>(
        $$"""{"Version":"test","Terminal":{"TerminalId":1,"OutletId":2,"BranchId":3,"TerminalName":"Till","BranchTaxType":"Non-VAT","DeviceIdentifier":"TEST","MenuDisplayMode":{{mode}}},"Cashiers":[{"UserId":1,"Username":"test","Role":"Cashier","PinHash":"hash"}],"Products":[{"ProductId":1,"Name":"Item","ImageUrl":"https://invalid.example/item.png"}]}""")!;

    private static int Mode(object config) => Convert.ToInt32(config.GetType().GetProperty("MenuDisplayMode")?.GetValue(config)
        ?? throw new InvalidOperationException("MenuDisplayMode must be persisted and mapped."));

    private static async Task SuccessfulSync()
    {
        using var db = NewDatabase();
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Payload(1)) });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://invalid.example/") };
        Program.Check(await new ReferenceDataSyncService(db).SyncReferenceDataAsync(1, http), "Reference sync failed.");
        using var verify = new LocalPosDbContext();
        Program.Check(Mode(await verify.TerminalConfigs.SingleAsync()) == 1, "Synced ImageAndText was not persisted.");
        Program.Check(handler.Requests.Count == 1 && handler.Requests[0] == "POST /api/offline/reference-data?terminalId=1", "Mode/image propagation added HTTP requests.");
    }

    private static async Task PrinterPreservation()
    {
        using var db = NewDatabase();
        db.TerminalConfigs.Add(new LocalTerminalConfig { TerminalId = 1, OutletId = 2, BranchId = 3, TerminalName = "Till", TerminalIdentifier = "TEST", ReceiptPrinterName = "Receipt", KitchenPrinterName = "Kitchen", BarPrinterName = "Bar" });
        await db.SaveChangesAsync();
        using var http = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Payload(1)) })) { BaseAddress = new Uri("https://invalid.example/") };
        Program.Check(await new ReferenceDataSyncService(db).SyncReferenceDataAsync(1, http), "Reference sync failed.");
        using var verify = new LocalPosDbContext();
        var config = await verify.TerminalConfigs.SingleAsync();
        Program.Check(config.ReceiptPrinterName == "Receipt" && config.KitchenPrinterName == "Kitchen" && config.BarPrinterName == "Bar", "Reference sync reset device-local printer assignments.");
    }

    private static async Task SessionReload()
    {
        using var db = NewDatabase();
        var cached = new LocalTerminalConfig { TerminalId = 1, OutletId = 2, BranchId = 3, TerminalName = "Till", TerminalIdentifier = "TEST", MenuDisplayMode = MenuDisplayMode.ImageAndText };
        db.TerminalConfigs.Add(cached); await db.SaveChangesAsync();
        var session = new PosSessionService();
        session.SetSession("OFFLINE-SESSION-TOKEN", "cashier", "Cashier", new TerminalConfigDto { TerminalId = 1, OutletId = 2, BranchId = 3 });
        var refresh = typeof(PosSessionService).GetMethod("RefreshCachedMenuDisplayModeAsync");
        Program.Check(refresh != null, "Login needs a cached mode refresh before navigation.");
        await (Task)refresh!.Invoke(session, null)!;
        Program.Check(session.Terminal!.MenuDisplayMode == MenuDisplayMode.ImageAndText, "Session ignored authoritative cached mode.");
        cached.MenuDisplayMode = (MenuDisplayMode)987; await db.SaveChangesAsync();
        await (Task)refresh.Invoke(session, null)!;
        Program.Check(session.Terminal.MenuDisplayMode == MenuDisplayMode.TextOnly, "Unknown cached mode did not fail closed.");
        db.TerminalConfigs.Remove(cached); await db.SaveChangesAsync();
        session.Terminal.MenuDisplayMode = MenuDisplayMode.ImageAndText;
        await (Task)refresh.Invoke(session, null)!;
        Program.Check(session.Terminal.MenuDisplayMode == MenuDisplayMode.TextOnly, "Missing cached mode did not default to text.");
    }

    private static Task LoginWiring()
    {
        var login = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ClientSources", "Login.razor"));
        var layout = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ClientSources", "MainLayout.razor"));
        Program.Check(!login.Contains("existingOutlet.MenuDisplayMode = outlet.MenuDisplayMode"), "Resolve-before-sync must not overwrite a prior cached setting on sync failure.");
        Program.Check(login.Contains("MenuDisplayMode = outlet.MenuDisplayMode"), "Online cache insertion omits menu mode.");
        Program.Check(login.Contains("MenuDisplayMode = localOutlet.MenuDisplayMode"), "Offline login mapping omits menu mode.");
        var sync = login.IndexOf("var syncResult = await ReferenceDataSync.SyncReferenceDataAsync", StringComparison.Ordinal);
        var reload = login.IndexOf("await Session.RefreshCachedMenuDisplayModeAsync();", sync, StringComparison.Ordinal);
        var navigate = login.IndexOf("Navigation.NavigateTo(\"/\");", sync, StringComparison.Ordinal);
        Program.Check(reload > sync && reload < navigate, "Online login must reload committed cache after sync and before navigation.");
        Program.Check(layout.Contains("MenuDisplayMode = outlet.MenuDisplayMode"), "Lock fallback insertion omits menu mode.");
        return Task.CompletedTask;
    }

    private static async Task OldSchema()
    {
        using var db = NewDatabase(initialize: false);
        // Construct the pre-feature schema before InitializeDatabase's process/path guard runs.
        db.Database.EnsureCreated();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE LocalTerminalConfigs DROP COLUMN MenuDisplayMode;");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO LocalTerminalConfigs (TerminalId, OutletId, OutletName, TerminalCode, BranchId, TerminalName, TerminalIdentifier, BranchTaxType, ReceiptPrinterName) VALUES (1,2,'Outlet','T1',3,'Till','TEST','Non-VAT','Receipt');");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO LocalInvoiceSequences (SequenceId, TerminalId, SeriesCode, Prefix, StartingNumber, EndingNumber, LastIssuedNumber, PaddingLength, IsActive) VALUES (42,1,'SER','I-',1,1000,127,7,1);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO LocalSyncOutboxes (OutboxId, EntityType, EntityId, PayloadJson, Status, RetryCount, CreatedAt) VALUES (7,'Order','original',{0},'PENDING',2,'2026-01-01');", "{\"invoice\":\"I-0000127\"}");
        db.InitializeDatabase(); db.InitializeDatabase();
        var config = await db.TerminalConfigs.AsNoTracking().SingleAsync();
        Program.Check(config.MenuDisplayMode == MenuDisplayMode.TextOnly && config.ReceiptPrinterName == "Receipt" && config.OutletId == 2, "Additive upgrade changed terminal configuration.");
        Program.Check((await db.InvoiceSequences.SingleAsync()).LastIssuedNumber == 127, "Additive upgrade reset invoice counter.");
        var outbox = await db.SyncOutboxes.SingleAsync();
        Program.Check(outbox.PayloadJson == "{\"invoice\":\"I-0000127\"}" && outbox.RetryCount == 2 && outbox.Status == "PENDING", "Additive upgrade mutated pending fiscal payload.");
        var columns = await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_table_info('LocalTerminalConfigs') WHERE name = 'MenuDisplayMode' AND [notnull] = 1 AND dflt_value = '0'").SingleAsync();
        Program.Check(columns == 1, "Upgrade needs one non-null INTEGER mode column defaulting to zero.");
    }

    private static async Task FailedSyncs()
    {
        using var db = NewDatabase();
        db.TerminalConfigs.Add(new LocalTerminalConfig { TerminalId = 1, OutletId = 2, BranchId = 3, TerminalName = "Prior", TerminalIdentifier = "TEST", MenuDisplayMode = MenuDisplayMode.ImageAndText, ReceiptPrinterName = "Receipt" });
        db.Products.Add(new LocalProduct { ProductId = 99, Name = "Prior product" });
        db.InvoiceSequences.Add(new LocalInvoiceSequence { SequenceId = 42, TerminalId = 1, SeriesCode = "SER", Prefix = "I-", LastIssuedNumber = 127, EndingNumber = 1000 });
        db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "Order", EntityId = "original", PayloadJson = "unchanged", Status = "PENDING" });
        await db.SaveChangesAsync();
        var invalidTerminal = Payload(0); invalidTerminal.Terminal.TerminalId = 2;
        var invalidOutlet = Payload(0); invalidOutlet.Terminal.OutletId = 0;
        var invalidBranch = Payload(0); invalidBranch.Terminal.BranchId = 0;
        var invalidCollections = Payload(0); invalidCollections.Products = null!;
        var duplicate = Payload(0); duplicate.Products.Add(duplicate.Products[0]);
        foreach (var (name, response) in new (string, Func<HttpResponseMessage>)[]
        {
            ("HTTP 500", () => new(HttpStatusCode.InternalServerError)),
            ("Malformed JSON", () => new(HttpStatusCode.OK) { Content = new StringContent("not-json") }),
            ("Missing terminal", () => new(HttpStatusCode.OK) { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") }),
            ("Wrong terminal", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(invalidTerminal) }),
            ("Missing outlet", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(invalidOutlet) }),
            ("Missing branch", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(invalidBranch) }),
            ("Null products", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(invalidCollections) }),
            ("Duplicate products rollback", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(duplicate) }),
            ("SQLite write failure rollback", () => new(HttpStatusCode.OK) { Content = JsonContent.Create(Payload(0)) })
        })
        {
            if (name == "SQLite write failure rollback")
                await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectMenuReferenceInsert BEFORE INSERT ON LocalTerminalConfigs BEGIN SELECT RAISE(ABORT, 'test rollback'); END;");
            var handler = new ResponseHandler(response);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://invalid.example/") };
            Program.Check(!await new ReferenceDataSyncService(db).SyncReferenceDataAsync(1, http), name + " should fail sync.");
            using var verify = new LocalPosDbContext();
            var saved = await verify.TerminalConfigs.AsNoTracking().SingleAsync();
            Program.Check(saved.MenuDisplayMode == MenuDisplayMode.ImageAndText && saved.TerminalName == "Prior" && saved.ReceiptPrinterName == "Receipt", name + " changed committed config.");
            Program.Check((await verify.Products.SingleAsync()).ProductId == 99 && !await verify.SyncAuditLogs.AnyAsync(), name + " partially replaced reference data.");
            Program.Check((await verify.InvoiceSequences.SingleAsync()).LastIssuedNumber == 127 && (await verify.SyncOutboxes.SingleAsync()).PayloadJson == "unchanged", name + " interfered with fiscal state.");
            var session = new PosSessionService(); session.SetSession("token", "test", "Cashier", new TerminalConfigDto { TerminalId = 1, OutletId = 2, BranchId = 3 });
            await session.RefreshCachedMenuDisplayModeAsync();
            Program.Check(session.Terminal!.MenuDisplayMode == MenuDisplayMode.ImageAndText, name + " lost cached mode in session.");
            Program.Check(handler.Requests.Count == 1, name + " unexpectedly added requests.");
        }
    }

    private static async Task TextAndUnknown()
    {
        using var db = NewDatabase();
        foreach (var mode in new[] { 0, 99 })
        {
            var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Payload(mode)) });
            using var http = new HttpClient(handler) { BaseAddress = new Uri("https://invalid.example/") };
            Program.Check(await new ReferenceDataSyncService(db).SyncReferenceDataAsync(1, http), "Text/unknown sync failed.");
            using var verify = new LocalPosDbContext();
            Program.Check((await verify.TerminalConfigs.AsNoTracking().SingleAsync()).MenuDisplayMode == MenuDisplayMode.TextOnly, "Unknown/default setting did not fail closed.");
            Program.Check((await verify.Products.SingleAsync()).ImageUrl == "https://invalid.example/item.png" && handler.Requests.Count == 1, "Image metadata must be cached without downloading files.");
        }
    }

    private static async Task ClientDeviceActivation()
    {
        using var db = NewDatabase();
        var handler = new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new TerminalActivationResponseDto
            {
                Success = true,
                Message = "Activated",
                Config = new TerminalConfigDto
                {
                    TerminalId = 5,
                    OutletId = 2,
                    OutletName = "Main Dining",
                    BranchId = 1,
                    BranchName = "HQ",
                    BranchCode = "HQ01",
                    TerminalCode = "POS-05",
                    TerminalName = "Register 5",
                    PTUNumber = "PTU-999",
                    MINNumber = "MIN-888",
                    SerialNumber = "SN-777",
                    AllowSync = true
                }
            })
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://invalid.example/") };
        var terminalService = new POS.Shared.Services.TerminalService(http);
        var activationService = new DeviceActivationService(terminalService);

        Program.Check(!await activationService.IsDeviceProvisionedAsync(), "Fresh SQLite database must report unprovisioned.");
        var result = await activationService.ActivateDeviceAsync("654321");
        Program.Check(result.Success, "Activation should succeed.");
        Program.Check(await activationService.IsDeviceProvisionedAsync(), "Activated SQLite database must report provisioned.");

        var saved = await activationService.GetProvisionedTerminalAsync();
        Program.Check(saved != null && saved.IsActivated, "LocalTerminalConfig must be marked IsActivated.");
        Program.Check(!string.IsNullOrEmpty(saved!.DeviceUuid) && Guid.TryParse(saved.DeviceUuid, out _), "LocalTerminalConfig must store generated client UUID.");
        Program.Check(saved.TerminalCode == "POS-05" && saved.PTUNumber == "PTU-999" && saved.MINNumber == "MIN-888" && saved.SerialNumber == "SN-777", "BIR registration details and TerminalCode must be persisted in SQLite.");
    }

    private sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.Method + " " + request.RequestUri!.PathAndQuery);
            return Task.FromResult(response());
        }
    }
}
