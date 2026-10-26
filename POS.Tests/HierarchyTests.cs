using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task Test24_HierarchyCrud()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        var outlets = new OutletsController(db);
        var duplicate = await outlets.CreateOutlet(new OutletDto { BranchId = 1, OutletCode = "main", Name = "Duplicate" });
        Assert(duplicate.Result is BadRequestObjectResult, "Outlet codes must be unique within a branch.");
        var created = await outlets.CreateOutlet(new OutletDto { BranchId = 1, OutletCode = "BOOTH", Name = "Booth", OutletType = "Booth" });
        Assert(created.Result is CreatedAtActionResult, "Physical outlet must be created.");
        var location = (OutletDto)((CreatedAtActionResult)created.Result!).Value!;
        var terminalApi = new TerminalsController(db, new ConfigurationBuilder().Build());
        var terminal = await terminalApi.CreateTerminal(new TerminalDto { OutletId = location.OutletId, TerminalCode = "K1", Name = "Kiosk", DeviceIdentifier = "192.168.1.50" });
        Assert(terminal.Result is CreatedAtActionResult, "Terminal must be assigned to its selected physical outlet.");
        var machine = (TerminalDto)((CreatedAtActionResult)terminal.Result!).Value!;
        Assert(machine.BranchId == 1 && machine.OutletId == location.OutletId, "Branch ownership must derive from physical outlet.");
        var repeat = await terminalApi.CreateTerminal(new TerminalDto { OutletId = location.OutletId, TerminalCode = "K2", Name = "Duplicate device", DeviceIdentifier = "192.168.1.50" });
        Assert(repeat.Result is BadRequestObjectResult, "Duplicate device registration must be rejected.");
        Assert(await outlets.DeleteOutlet(location.OutletId) is ConflictObjectResult, "Outlet with a terminal must not be deleted.");
        Assert(await terminalApi.DeleteTerminal(1) is ConflictObjectResult, "Terminal with a historical shift must not be deleted.");
        var wrongBranch = await terminalApi.CreateTerminal(new TerminalDto { OutletId = location.OutletId, BranchId = 999, TerminalCode = "K3", Name = "Wrong branch" });
        Assert(wrongBranch.Result is BadRequestObjectResult, "Cross-branch terminal assignment must be rejected.");
        Assert(await terminalApi.DeleteTerminal(machine.TerminalId) is NoContentResult, "Empty terminal can be deleted.");
        Assert(await outlets.DeleteOutlet(location.OutletId) is NoContentResult, "Empty physical outlet can be deleted.");
    }

    private static async Task Test25_LocalHierarchyUpgrade()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE LocalOutletConfigs (OutletId INTEGER PRIMARY KEY, BranchId INTEGER, OutletName TEXT);
            INSERT INTO LocalOutletConfigs VALUES (42, 7, 'Old register');
            CREATE TABLE LocalOrders (OrderId TEXT PRIMARY KEY, OutletId INTEGER, InvoiceNumber TEXT);
            INSERT INTO LocalOrders VALUES ('tx-old', 42, 'KEEP-000017');
            CREATE TABLE LocalInvoiceSequences (SequenceId INTEGER PRIMARY KEY, OutletId INTEGER, LastIssuedNumber INTEGER);
            INSERT INTO LocalInvoiceSequences VALUES (9, 42, 17);
            CREATE TABLE LocalSyncOutboxes (PayloadJson TEXT, Status TEXT);
            INSERT INTO LocalSyncOutboxes VALUES ('{"OutletId":42,"CashierId":"original"}', 'RETRY_PENDING');
            """;
        await cmd.ExecuteNonQueryAsync();
        cmd.CommandText = "SELECT PayloadJson FROM LocalSyncOutboxes";
        var before = (string)(await cmd.ExecuteScalarAsync())!;
        POS.Client.Data.LocalHierarchyUpgrade.Apply(connection);
        POS.Client.Data.LocalHierarchyUpgrade.Apply(connection);
        cmd.CommandText = "SELECT TerminalId FROM LocalOrders WHERE InvoiceNumber='KEEP-000017'";
        Assert(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 42, "Upgrade must retain terminal identity and invoice bytes.");
        cmd.CommandText = "SELECT LastIssuedNumber FROM LocalInvoiceSequences WHERE TerminalId=42";
        Assert(Convert.ToInt64(await cmd.ExecuteScalarAsync()) == 17, "Invoice counter must not reset.");
        cmd.CommandText = "SELECT PayloadJson FROM LocalSyncOutboxes WHERE Status='RETRY_PENDING'";
        Assert((string)(await cmd.ExecuteScalarAsync())! == before, "Pending payload and status must remain byte-for-byte unchanged.");
        cmd.CommandText = "SELECT TerminalName FROM LocalTerminalConfigs WHERE TerminalId=42";
        Assert((string)(await cmd.ExecuteScalarAsync())! == "Old register", "Cached machine name must survive upgrade.");
        cmd.CommandText = "SELECT OutletId FROM LocalTerminalConfigs WHERE TerminalId=42";
        Assert(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 0, "Unknown physical location must not be guessed from a terminal ID.");
    }

    private static Task Test26_LegacyPayload()
    {
        const string legacy = "{\"OutletId\":42,\"BranchId\":7,\"CashierId\":\"saved\"}";
        var dto = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>(legacy)!;
        Assert(dto.TerminalId == 42 && dto.BranchId == 7, "Old snapshots must deserialize to the preserved terminal ID.");
        var modern = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>("{\"TerminalId\":12,\"OutletId\":9}")!;
        Assert(modern.TerminalId == 12, "New terminal identity must not be overwritten by physical outlet identity.");
        return Task.CompletedTask;
    }
    private static void Authenticate(ControllerBase controller, string role = "Admin", string terminalId = "1")
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
        { User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(new[]
        {
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "cashier1"),
            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role),
            new System.Security.Claims.Claim("terminal_id", terminalId), new System.Security.Claims.Claim("branch_id", "1")
        }, "test")) } };
    }

    private static async Task Test27_SaleReceipt()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        var terminal = await db.Terminals.SingleAsync();
        terminal.DeviceIdentifier = "192.168.1.50";
        terminal.Ptunumber = "KEEP-PTU";
        (await db.Branches.SingleAsync()).Code = "BR1";
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "TEST", Prefix = "T-", StartingNumber = 1, EndingNumber = 1000, LastIssuedNumber = 0 });
        await db.SaveChangesAsync();
        var resolver = new TerminalsController(db, new ConfigurationBuilder().Build());
        Authenticate(resolver);
        var resolved = await resolver.ResolveTerminal("192.168.1.50") as OkObjectResult;
        Assert(resolved?.Value is TerminalConfigDto config && config.TerminalId == 1 && config.OutletId == 1, "Resolution must return all three ownership levels.");
        var sequences = new POS.Api.Services.SequenceService(db);
        var orders = new OrdersController(db, sequences, new POS.Api.Services.DiscountCalculationService());
        Authenticate(orders);
        var result = await orders.CreateOrder(new OrderCreateDto { BranchId = 1, TerminalId = 1, CashierId = "cashier1", OrderType = "RETAIL",
            Items = new() { new OrderItemCreateDto { ProductId = 1, ProductName = "Sample", Quantity = 1, BasePrice = 112, TaxName = "VATable" } } });
        Assert(result.Result is OkObjectResult, "Sale creation failed: " + (result.Result as ObjectResult)?.Value);
        var order = (OrderResponseDto)((OkObjectResult)result.Result!).Value!;
        var payments = new PaymentsController(db, sequences); Authenticate(payments);
        var paid = await payments.ProcessPayment(new PaymentProcessDto { OrderId = order.OrderId, AmountTendered = 120, ProcessedBy = "cashier1" });
        Assert(paid.Result is OkObjectResult, "Payment failed: " + (paid.Result as ObjectResult)?.Value);
        var receipt = await new POS.Api.Services.ReceiptService(db).GetReceiptAsync(order.OrderId);
        Assert(receipt?.InvoiceNumber == "T-0000001" && receipt.TerminalId == 1 && receipt.OutletId == 1 && receipt.PTUNumber == "KEEP-PTU", "Receipt must preserve terminal numbering and machine configuration.");
        Assert((await payments.ProcessPayment(new PaymentProcessDto { OrderId = order.OrderId })).Result is ConflictObjectResult, "Payment replay must not issue another invoice.");
    }

    private static async Task Test28_OfflineReplay()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "OFF", Prefix = "OFF-", StartingNumber = 1, EndingNumber = 1000, LastIssuedNumber = 0 });
        await db.SaveChangesAsync();
        var api = new OfflineSyncController(db); Authenticate(api);
        var request = new OfflineTransactionSyncRequest { LocalTransactionId = "original-tx", TerminalId = 1, BranchId = 1, CashierId = "cashier1",
            DeviceIdentifier = "TEST", InvoiceNumber = "OFF-0000017", PayloadHash = "unchanged-original-hash", TransactionDate = DateTime.UtcNow,
            Subtotal = 112, TotalAmount = 112, VatableSales = 100, VatAmount = 12,
            Payments = new() { new OfflinePaymentDto { Amount = 112, TenderedAmount = 120, ChangeAmount = 8, PaidAt = DateTime.UtcNow } } };
        var first = await api.SyncTransaction(request);
        Assert(first.Result is OkObjectResult, "Offline upload failed: " + (first.Result as ObjectResult)?.Value);
        Assert((await api.SyncTransaction(request)).Result is OkObjectResult, "Same terminal and hash must replay idempotently.");
        request.LocalTransactionId = "different-tx";
        Assert((await api.SyncTransaction(request)).Result is ConflictObjectResult, "Same invoice must not acknowledge another transaction.");
        Assert(await db.Orders.CountAsync() == 1 && (await db.InvoiceSequences.SingleAsync()).LastIssuedNumber == 17, "Offline upload must preserve one order and advance its terminal counter exactly once.");
    }

    private static async Task Test29_InvoiceReservations()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "RES", Prefix = "RES-", StartingNumber = 1, EndingNumber = 5000, LastIssuedNumber = 17 });
        await db.SaveChangesAsync();
        var reference = new OfflineReferenceDataController(db); Authenticate(reference);
        var first = (OfflineReferenceDataResponseDto)((OkObjectResult)(await reference.GetReferenceData(1)).Result!).Value!;
        Assert(first.InvoiceSequence?.StartingNumber == 18 && first.InvoiceSequence.EndingNumber == 1017, "Offline block must begin after all previously issued numbers.");
        Assert((await db.InvoiceSequences.SingleAsync()).LastIssuedNumber == 1017, "Server must reserve the complete offline block before responding.");
        var order = new Order { BranchId = 1, OutletId = 1, TerminalId = 1, OrderNumber = "RES-ORDER", CashierId = "cashier1", OrderType = "RETAIL", OrderStatus = "COMPLETED", PaymentStatus = "PAID" };
        db.Orders.Add(order); await db.SaveChangesAsync();
        var invoice = await new POS.Api.Services.SequenceService(db).IssueInvoiceNumberAsync(order.OrderId, 1);
        Assert(invoice == "RES-0001018", "Online invoice must skip the offline reservation.");
        var second = (OfflineReferenceDataResponseDto)((OkObjectResult)(await reference.GetReferenceData(1)).Result!).Value!;
        Assert(second.InvoiceSequence?.StartingNumber == 1019 && second.InvoiceSequence.StartingNumber > first.InvoiceSequence!.EndingNumber, "Refresh must reserve a distinct range, not reset or reuse a counter.");
    }

    private static async Task Test30_RegistrationAddressCollision()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        var original = await db.Terminals.SingleAsync(); original.DeviceIdentifier = "device-a"; original.IpAddress = "10.10.0.1";
        await db.SaveChangesAsync();
        var api = new TerminalsController(db, new ConfigurationBuilder().Build());
        var result = await api.CreateTerminal(new TerminalDto { OutletId = 1, TerminalCode = "COLLISION", Name = "Collision",
            DeviceIdentifier = "10.10.0.1", IpAddress = "10.10.0.2" });
        Assert(result.Result is BadRequestObjectResult, "A device IP must not disagree with its canonical registration IP or alias another terminal.");
    }

    private static async Task Test31_UnscopedOfflineWriteIsForbidden()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        var api = new OfflineSyncController(db); Authenticate(api, "Cashier");
        ((System.Security.Claims.ClaimsIdentity)api.User.Identity!).RemoveClaim(api.User.FindFirst("terminal_id")!);
        Assert((await api.SyncTransaction(new OfflineTransactionSyncRequest { LocalTransactionId = "attack", TerminalId = 1 })).Result is ForbidResult, "Missing terminal claim must not permit offline transaction writes.");
        Assert((await api.SyncCashMovement(new OfflineCashMovementSyncRequest { ClientMovementId = "attack", TerminalId = 1, Amount = 1, Reason = "x" })).Result is ForbidResult, "Missing terminal claim must not permit offline cash writes.");
    }

    private static async Task Test32_InactiveHistoryRecovery()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "REC", Prefix = "REC-", StartingNumber = 1, EndingNumber = 1000 });
        var terminal = await db.Terminals.SingleAsync(); terminal.IsActive = false; terminal.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var api = new OfflineSyncController(db); Authenticate(api);
        var request = new OfflineTransactionSyncRequest { LocalTransactionId = "recover-old", TerminalId = 1, BranchId = 1, CashierId = "cashier1", DeviceIdentifier = "saved", InvoiceNumber = "REC-0000017", PayloadHash = "saved-hash", TransactionDate = DateTime.UtcNow.AddMinutes(-5) };
        Assert((await api.SyncTransaction(request, "Reconcile before retirement")).Result is OkObjectResult, "Supervisor must be able to reconcile completed historical work for an inactive terminal.");
        Assert(await db.TerminalRecoveryLogs.CountAsync() == 1, "Historical recovery must leave a durable supervisor/reason audit trail.");
    }

    private static Task Test33_ExactAcknowledgement()
    {
        var request = new OfflineTransactionSyncRequest { LocalTransactionId = "id", InvoiceNumber = "INV-1", TerminalId = 42, PayloadHash = "original" };
        var status = new OfflineTransactionStatusDto { Status = "SYNCED", LocalTransactionId = "id", InvoiceNumber = "INV-1", TerminalId = 42, PayloadHash = "changed", ServerOrderId = 1 };
        Assert(!POS.Shared.Services.OfflineSyncAcknowledgement.Matches(request, status), "Status lookup must not acknowledge a different pending payload.");
        status.PayloadHash = "original";
        Assert(POS.Shared.Services.OfflineSyncAcknowledgement.Matches(request, status), "Only exact identity/hash acknowledgement is accepted.");
        status.TerminalId = 99;
        Assert(!POS.Shared.Services.OfflineSyncAcknowledgement.Matches(request, status), "Another terminal's status must not acknowledge this payload.");
        var response = new OfflineTransactionSyncResponse { Success = true, LocalTransactionId = "id", InvoiceNumber = "INV-1", ServerOrderId = 5 };
        Assert(POS.Shared.Services.OfflineSyncAcknowledgement.Matches(request, response), "Matching direct response must be accepted.");
        response.InvoiceNumber = "WRONG";
        Assert(!POS.Shared.Services.OfflineSyncAcknowledgement.Matches(request, response), "Mismatched invoice in direct response must be rejected.");
        return Task.CompletedTask;
    }

    private static async Task Test34_CashMovementStatusScoping()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.CashDrawerMovements.Add(new CashDrawerMovement
        {
            MovementId = 1, TerminalId = 1, BranchId = 1, ShiftId = 1, ClientMovementId = "cmv-scope-1",
            MovementType = "PAID_IN", Amount = 50, Status = "COMPLETED", Reason = "Change float", AuthorizedBy = "cashier1", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var api = new OfflineSyncController(db);
        Authenticate(api, "Cashier", terminalId: "999");
        var forbiddenResult = await api.GetCashMovementSyncStatus("cmv-scope-1");
        Assert(forbiddenResult is ForbidResult, "Cashier from another terminal must not access cash movement status.");

        Authenticate(api, "Cashier", terminalId: "1");
        var okResult = await api.GetCashMovementSyncStatus("cmv-scope-1");
        Assert(okResult is OkObjectResult, "Cashier from the same terminal can access cash movement status.");

        Authenticate(api, "Manager", terminalId: "999");
        var supervisorResult = await api.GetCashMovementSyncStatus("cmv-scope-1");
        Assert(supervisorResult is OkObjectResult, "Supervisor from another terminal can access cash movement status.");
    }

    private static async Task Test35_IdempotentReplayCorrelation()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.Orders.Add(new Order { OrderId = 1, OrderNumber = "ORD-1", InvoiceNumber = "INV-001", BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "c1", OrderType = "RETAIL", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 100, TotalAmount = 100, VatableSales = 100, VatAmount = 0, CreatedAt = DateTime.UtcNow });
        db.Orders.Add(new Order { OrderId = 2, OrderNumber = "ORD-2", InvoiceNumber = "INV-002", BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "c1", OrderType = "RETAIL", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 200, TotalAmount = 200, VatableSales = 200, VatAmount = 0, CreatedAt = DateTime.UtcNow });
        db.OfflineSyncIdempotencyRecords.Add(new OfflineSyncIdempotencyRecord { LocalTransactionId = "local-A", InvoiceNumber = "INV-001", PayloadHash = "hash-A", ServerOrderId = 1, ProcessedAt = DateTime.UtcNow });
        db.OfflineSyncIdempotencyRecords.Add(new OfflineSyncIdempotencyRecord { LocalTransactionId = "local-B", InvoiceNumber = "INV-002", PayloadHash = "hash-B", ServerOrderId = 2, ProcessedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var api = new OfflineSyncController(db); Authenticate(api);
        // Upload with local-A and hash-A, but claiming INV-002 (Order 2's invoice!)
        var attackReq = new OfflineTransactionSyncRequest { LocalTransactionId = "local-A", InvoiceNumber = "INV-002", TerminalId = 1, PayloadHash = "hash-A" };
        var attackResult = await api.SyncTransaction(attackReq);
        Assert(attackResult.Result is ConflictObjectResult, "Cross-correlated replay must return 409 Conflict, never acknowledge another order.");

        // Exact replay with local-A, INV-001, hash-A
        var exactReq = new OfflineTransactionSyncRequest { LocalTransactionId = "local-A", InvoiceNumber = "INV-001", TerminalId = 1, PayloadHash = "hash-A" };
        var exactResult = await api.SyncTransaction(exactReq);
        Assert(exactResult.Result is OkObjectResult ok && ((OfflineTransactionSyncResponse)ok.Value!).ServerOrderId == 1, "Exact replay must acknowledge the original correlated order.");
    }

    private static async Task Test36_SupervisorRecoveryAcrossTerminals()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "MGR", Prefix = "MGR-", StartingNumber = 1, EndingNumber = 1000 });
        var terminal = await db.Terminals.SingleAsync(); terminal.IsActive = false; terminal.DeactivatedAt = DateTime.UtcNow; terminal.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var api = new OfflineSyncController(db);
        // Supervisor is logged into terminal 999 (manager station)
        Authenticate(api, "Manager", terminalId: "999");
        var request = new OfflineTransactionSyncRequest { LocalTransactionId = "mgr-rec-1", TerminalId = 1, BranchId = 1, CashierId = "cashier1", DeviceIdentifier = "saved", InvoiceNumber = "MGR-0000001", PayloadHash = "hash-1", TransactionDate = DateTime.UtcNow.AddMinutes(-5) };
        var result = await api.SyncTransaction(request, "Reconciling decommissioned terminal");
        Assert(result.Result is OkObjectResult, "Supervisor logged into terminal 999 must be permitted to reconcile historical work for terminal 1.");
    }

    private static async Task Test37_DeactivationCutoffPreserved()
    {
        var (conn, db, _) = await CreateTestEnvAsync(); await using var cleanup = conn;
        db.InvoiceSequences.Add(new InvoiceSequence { TerminalId = 1, SeriesCode = "CUT", Prefix = "CUT-", StartingNumber = 1, EndingNumber = 1000 });
        var terminal = await db.Terminals.SingleAsync();
        var deactivationTime = DateTime.UtcNow.AddHours(-2);
        terminal.IsActive = false;
        terminal.DeactivatedAt = deactivationTime;
        terminal.UpdatedAt = deactivationTime;
        await db.SaveChangesAsync();

        // Later, someone edits the inactive terminal's metadata (e.g. renames it)
        var terminalsApi = new TerminalsController(db, new ConfigurationBuilder().Build());
        Authenticate(terminalsApi, "Admin");
        await terminalsApi.UpdateTerminal(1, new TerminalDto { OutletId = 1, BranchId = 1, TerminalCode = "POS-1", Name = "Renamed Retired Machine", IsActive = false, DeviceIdentifier = "saved-dev" });

        var reloaded = await db.Terminals.FindAsync(1);
        Assert(reloaded!.DeactivatedAt == deactivationTime, "Editing inactive terminal metadata must preserve original DeactivatedAt timestamp.");
        Assert(reloaded.UpdatedAt > deactivationTime, "UpdatedAt should reflect the recent edit.");

        // Attempt to upload work dated 1 hour ago (AFTER deactivation, even though before the recent rename)
        var api = new OfflineSyncController(db); Authenticate(api, "Admin");
        var lateRequest = new OfflineTransactionSyncRequest { LocalTransactionId = "late-sale", TerminalId = 1, BranchId = 1, CashierId = "cashier1", DeviceIdentifier = "saved-dev", InvoiceNumber = "CUT-0000001", PayloadHash = "hash-late", TransactionDate = DateTime.UtcNow.AddHours(-1) };
        var lateResult = await api.SyncTransaction(lateRequest, "Attempt late recovery");
        Assert(lateResult.Result is ConflictObjectResult conflict && conflict.Value!.ToString()!.Contains("Recovery accepts only historical work completed before deactivation"), "Transactions dated after deactivation must be rejected even if terminal was edited later.");
    }

    private static Task Test38_ReceiptFormattingOutput()
    {
        var receipt = new ReceiptDto
        {
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            OrderNumber = "ORD-0001",
            InvoiceNumber = "INV-0000105",
            TerminalName = "POS-01",
            CashierId = "cashier1",
            OrderType = "DINING",
            TableNumber = "12",
            HeadCount = 4,
            CreatedAt = DateTime.UtcNow,
            PaidAt = DateTime.UtcNow,
            Subtotal = 310m,
            TotalAmount = 310m,
            VatableSales = 276.78m,
            VatAmount = 33.22m,
            Items = new()
            {
                new OrderItemResponseDto { ProductName = "Burger Deluxe", ProductShortCode = "BURGER-DLX", Quantity = 1, UnitPrice = 250m, LineTotal = 250m },
                new OrderItemResponseDto { ProductName = "Iced Tea", Quantity = 1, UnitPrice = 60m, LineTotal = 60m }
            },
            Payments = new()
            {
                new PaymentResponseDto { PaymentMethod = "CASH", Amount = 310m, AmountTendered = 500m, ChangeAmount = 190m }
            }
        };

        var html = POS.Client.Services.ReceiptHtmlFormatter.Format(receipt);
        Assert(!html.Contains(".Append("), "Formatted receipt must not contain raw C# source artifacts like .Append(");
        Assert(html.Contains("Main Branch"), "Receipt must contain branch name.");
        Assert(html.Contains("Type: DINING | Table: 12 | Guests: 4"), "Receipt must render service mode, table number, and guest count.");
        Assert(html.Contains("Total Items"), "Receipt must contain Total Items row.");
        Assert(html.Contains("BURGER-DLX"), "Receipt must render item short code / name.");
        Assert(html.Contains("PHP 310.00"), "Receipt must format currency totals.");
        return Task.CompletedTask;
    }

    private static Task Test40_BarAndKitchenRouting()
    {
        var receipt = new ReceiptDto
        {
            OrderId = 999,
            OrderNumber = "ORD-TEST-40",
            InvoiceNumber = "INV-TEST-40",
            TerminalName = "POS-01",
            CashierId = "cashier1",
            OrderType = "DINING",
            TableNumber = "5",
            HeadCount = 2,
            CreatedAt = DateTime.UtcNow,
            Items = new()
            {
                new OrderItemResponseDto { ProductId = 1, ProductName = "Ribeye Steak", Quantity = 1, IsKitchenItem = true, IsBarItem = false },
                new OrderItemResponseDto { ProductId = 2, ProductName = "Classic Mojito", Quantity = 2, IsKitchenItem = false, IsBarItem = true },
                new OrderItemResponseDto { ProductId = 3, ProductName = "Irish Coffee Combo", Quantity = 1, IsKitchenItem = true, IsBarItem = true, Modifiers = new() { new() { ModifierName = "Extra Shot" } } },
                new OrderItemResponseDto { ProductId = 4, ProductName = "Souvenir T-Shirt", Quantity = 1, IsKitchenItem = false, IsBarItem = false }
            }
        };

        // 1. Generate tickets with independent assigned printers
        var tickets = POS.Client.Services.PreparationTicketRouter.GenerateTickets(receipt, "Kitchen-Thermal-1", "Bar-Thermal-1");
        Assert(tickets.Count == 2, $"Expected exactly 2 preparation tickets (Kitchen and Bar), got {tickets.Count}");

        var kitchen = tickets.FirstOrDefault(t => t.Station == "KITCHEN");
        Assert(kitchen != null, "Kitchen ticket must be generated.");
        Assert(kitchen!.AssignedPrinterName == "Kitchen-Thermal-1", "Kitchen ticket must receive assigned kitchen printer.");
        Assert(kitchen.OrderNumber == "ORD-TEST-40", "Kitchen ticket must preserve exact order number.");
        Assert(kitchen.TableNumber == "5" && kitchen.HeadCount == 2, "Kitchen ticket must preserve table and guest metadata.");
        Assert(kitchen.Items.Count == 2, "Kitchen ticket must contain exactly 2 items (Steak and Combo).");
        Assert(kitchen.Items.Any(i => i.ProductName == "Ribeye Steak"), "Kitchen ticket must contain Ribeye Steak.");
        Assert(kitchen.Items.Any(i => i.ProductName == "Irish Coffee Combo"), "Kitchen ticket must contain combined Combo item.");
        Assert(!kitchen.Items.Any(i => i.ProductName == "Classic Mojito"), "Kitchen ticket must NOT contain Bar-only Mojito.");
        Assert(!kitchen.Items.Any(i => i.ProductName == "Souvenir T-Shirt"), "Kitchen ticket must NOT contain non-preparation T-Shirt.");

        var bar = tickets.FirstOrDefault(t => t.Station == "BAR");
        Assert(bar != null, "Bar ticket must be generated.");
        Assert(bar!.AssignedPrinterName == "Bar-Thermal-1", "Bar ticket must receive assigned bar printer.");
        Assert(bar.OrderNumber == "ORD-TEST-40", "Bar ticket must preserve exact order number identical to kitchen.");
        Assert(bar.TableNumber == "5" && bar.HeadCount == 2, "Bar ticket must preserve table and guest metadata.");
        Assert(bar.Items.Count == 2, "Bar ticket must contain exactly 2 items (Mojito and Combo).");
        Assert(bar.Items.Any(i => i.ProductName == "Classic Mojito"), "Bar ticket must contain Classic Mojito.");
        Assert(bar.Items.Any(i => i.ProductName == "Irish Coffee Combo"), "Bar ticket must contain combined Combo item.");
        Assert(!bar.Items.Any(i => i.ProductName == "Ribeye Steak"), "Bar ticket must NOT contain Kitchen-only Steak.");
        Assert(!bar.Items.Any(i => i.ProductName == "Souvenir T-Shirt"), "Bar ticket must NOT contain non-preparation T-Shirt.");

        // 2. Format HTML output and check station headers & BIR segregation
        var kHtml = POS.Client.Services.PreparationTicketHtmlFormatter.Format(kitchen);
        Assert(kHtml.Contains("*** KITCHEN ORDER ***"), "Kitchen HTML must display prominent kitchen order header.");
        Assert(kHtml.Contains("Ribeye Steak"), "Kitchen HTML must render Ribeye Steak.");
        Assert(kHtml.Contains("ORD-TEST-40"), "Kitchen HTML must render Order Number.");
        Assert(kHtml.Contains("TABLE: 5"), "Kitchen HTML must render Table Number.");
        Assert(kHtml.Contains("PREPARATION TICKET"), "Preparation ticket must identify as preparation ticket.");
        Assert(!kHtml.Contains(".Append("), "Formatted preparation ticket must not contain code artifacts.");

        var bHtml = POS.Client.Services.PreparationTicketHtmlFormatter.Format(bar);
        Assert(bHtml.Contains("*** BAR ORDER ***"), "Bar HTML must display prominent bar order header.");
        Assert(bHtml.Contains("Classic Mojito"), "Bar HTML must render Classic Mojito.");
        Assert(bHtml.Contains("ORD-TEST-40"), "Bar HTML must render Order Number.");
        Assert(bHtml.Contains("TABLE: 5"), "Bar HTML must render Table Number.");
        Assert(!bHtml.Contains(".Append("), "Formatted bar ticket must not contain code artifacts.");

        // 3. Retail-only order generates NO preparation tickets
        var retailReceipt = new ReceiptDto
        {
            OrderNumber = "ORD-RETAIL",
            Items = new() { new OrderItemResponseDto { ProductId = 10, ProductName = "Gift Bag", IsKitchenItem = false, IsBarItem = false } }
        };
        var noTickets = POS.Client.Services.PreparationTicketRouter.GenerateTickets(retailReceipt);
        Assert(noTickets.Count == 0, "Order with no preparation routing must not generate any preparation tickets.");

        // 4. Bar-only order generates ONLY Bar ticket
        var barOnlyReceipt = new ReceiptDto
        {
            OrderNumber = "ORD-BAR",
            Items = new() { new OrderItemResponseDto { ProductId = 11, ProductName = "Beer", IsKitchenItem = false, IsBarItem = true } }
        };
        var barTickets = POS.Client.Services.PreparationTicketRouter.GenerateTickets(barOnlyReceipt);
        Assert(barTickets.Count == 1 && barTickets[0].Station == "BAR", "Bar-only order must generate exactly 1 Bar ticket.");

        // 5. Kitchen-only order generates ONLY Kitchen ticket
        var kitchenOnlyReceipt = new ReceiptDto
        {
            OrderNumber = "ORD-KITCHEN",
            Items = new() { new OrderItemResponseDto { ProductId = 12, ProductName = "Fries", IsKitchenItem = true, IsBarItem = false } }
        };
        var kitchenTickets = POS.Client.Services.PreparationTicketRouter.GenerateTickets(kitchenOnlyReceipt);
        Assert(kitchenTickets.Count == 1 && kitchenTickets[0].Station == "KITCHEN", "Kitchen-only order must generate exactly 1 Kitchen ticket.");

        return Task.CompletedTask;
    }

}
