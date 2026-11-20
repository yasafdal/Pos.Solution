using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Helpers;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public partial class Program
{
    private sealed class VoidSyncRoutingHandler(OfflineSyncController syncController) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/offline-sync/transactions", StringComparison.OrdinalIgnoreCase))
            {
                var body = await request.Content!.ReadFromJsonAsync<OfflineTransactionSyncRequest>(cancellationToken: cancellationToken);
                var actionRes = await syncController.SyncTransaction(body!);
                return ConvertActionResult(actionRes);
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/api/offline-sync/voids", StringComparison.OrdinalIgnoreCase))
            {
                var body = await request.Content!.ReadFromJsonAsync<OfflineVoidSyncRequest>(cancellationToken: cancellationToken);
                var actionRes = await syncController.SyncVoid(body!, cancellationToken);
                return ConvertActionResult(actionRes);
            }

            if (request.Method == HttpMethod.Get && path.Contains("/api/offline-sync/voids/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
            {
                var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var clientVoidId = segments[^2];
                var actionRes = await syncController.GetVoidSyncStatus(clientVoidId, cancellationToken);
                return ConvertIActionResult(actionRes);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage ConvertActionResult<T>(ActionResult<T> actionResult)
        {
            if (actionResult.Result != null)
                return ConvertIActionResult(actionResult.Result);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(actionResult.Value)
            };
        }

        private static HttpResponseMessage ConvertIActionResult(IActionResult result)
        {
            if (result is ObjectResult obj)
            {
                var status = (HttpStatusCode)(obj.StatusCode ?? 200);
                return new HttpResponseMessage(status)
                {
                    Content = JsonContent.Create(obj.Value)
                };
            }
            if (result is StatusCodeResult sc)
            {
                return new HttpResponseMessage((HttpStatusCode)sc.StatusCode);
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        }
    }

    private static OfflineSyncController CreateOfflineSyncControllerForVoidTest(
        PosDbContext db,
        string username = "manager1",
        string role = "Manager",
        int? branchId = 1,
        int? terminalId = 1)
    {
        return new OfflineSyncController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username, role, branchId, terminalId)
                }
            }
        };
    }

    private static ReturnsController CreateReturnsControllerForVoidTest(
        PosDbContext db,
        string username = "manager1",
        string role = "Manager",
        int? branchId = 1,
        int? terminalId = 1)
    {
        return new ReturnsController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username, role, branchId, terminalId)
                }
            }
        };
    }

    private static PosSessionService CreateOfflineVoidSession(
        string username = "manager1",
        string role = "Manager",
        int branchId = 1,
        int outletId = 1,
        int terminalId = 1,
        string deviceId = "TERM-DEV-01")
    {
        var session = new PosSessionService();
        session.SetSession(
            token: "OFFLINE-SESSION-TOKEN",
            username: username,
            role: role,
            outlet: new TerminalConfigDto
            {
                TerminalId = terminalId,
                OutletId = outletId,
                BranchId = branchId,
                TerminalCode = $"POS-{terminalId:D2}",
                TerminalName = $"Counter {terminalId}",
                DeviceIdentifier = deviceId
            });
        return session;
    }

    private static async Task SeedLocalTerminalAndUsersAsync(
        LocalPosDbContext db,
        int branchId = 1,
        int outletId = 1,
        int terminalId = 1,
        string deviceId = "TERM-DEV-01",
        bool includeSupervisor = true,
        DateTime? supervisorSyncedAtUtc = null,
        decimal openingFloat = 1000m,
        long shiftId = 101,
        string? clientShiftId = "SHIFT-OFF-01")
    {
        var syncUtc = supervisorSyncedAtUtc ?? BusinessDayClock.UtcNow();

        db.Branches.Add(new LocalBranch { BranchId = branchId, Name = "Main Branch", Code = "BR-01", IsActive = true });
        db.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = terminalId,
            OutletId = outletId,
            BranchId = branchId,
            TerminalCode = $"POS-{terminalId:D2}",
            TerminalName = $"Counter {terminalId}",
            TerminalIdentifier = deviceId,
            IsInitialized = true,
            IsActivated = true,
            LastSyncAt = syncUtc
        });
        db.Cashiers.Add(new LocalCashier
        {
            UserId = 1,
            Username = "cashier1",
            Role = "Cashier",
            PinHash = VoidAuthorizationHelper.ComputePinHash("1111"),
            SyncedAtUtc = syncUtc
        });
        if (includeSupervisor)
        {
            db.Cashiers.Add(new LocalCashier
            {
                UserId = 2,
                Username = "manager1",
                Role = "Manager",
                PinHash = VoidAuthorizationHelper.ComputePinHash("9999"),
                SyncedAtUtc = syncUtc
            });
        }
        db.CashDrawerShifts.Add(new LocalCashDrawerShift
        {
            ShiftId = shiftId,
            ClientShiftId = clientShiftId,
            BranchId = branchId,
            TerminalId = terminalId,
            CashierId = "cashier1",
            OpeningFloat = openingFloat,
            ExpectedBalance = openingFloat,
            Status = "OPEN",
            OpeningConfirmed = true,
            OpenedAt = BusinessDayClock.UtcNow()
        });
        await db.SaveChangesAsync();
    }

    private static async Task TestVoidOff01_OnlineVoidRegressionAndRefundInventoryIntegrity()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var _ = conn;

        // Seed products, inventory, and an online order paid with split CASH (200) + GCASH (300)
        db.Categories.Add(new Category { CategoryId = 1, Name = "Meals", IsActive = true });
        db.Products.AddRange(
            new Product { ProductId = 10, CategoryId = 1, Name = "Burger", Price = 250m, IsActive = true, ProductType = "Food" },
            new Product { ProductId = 11, CategoryId = 1, Name = "Extra Cheese", Price = 50m, IsActive = true, ProductType = "Addon" });
        db.BranchInventories.AddRange(
            new BranchInventory { BranchId = 1, ProductId = 10, StockQuantity = 8, LastUpdated = DateTime.UtcNow },
            new BranchInventory { BranchId = 1, ProductId = 11, StockQuantity = 18, LastUpdated = DateTime.UtcNow });

        var shift = await db.CashDrawerShifts.FirstAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
        shift.OpeningFloat = 1000m;
        shift.TotalCashSales = 200m;
        shift.TotalCashRefunds = 0m;
        CashDrawerController.RecalculateShiftExpectedBalance(shift);

        var order = new Order
        {
            OrderNumber = "POS01-000010",
            InvoiceNumber = "INV01-0000010",
            BranchId = 1,
            OutletId = 1,
            TerminalId = 1,
            ShiftId = shift.ShiftId,
            CashierId = "cashier1",
            OrderType = "DINING",
            OrderStatus = "COMPLETED",
            PaymentStatus = PaymentStatuses.Paid,
            Subtotal = 500m,
            TotalAmount = 500m,
            CreatedAt = DateTime.UtcNow
        };
        var item = new OrderItem
        {
            ProductId = 10,
            ProductName = "Burger",
            Quantity = 2m,
            UnitPrice = 250m,
            BasePrice = 200m,
            ModifierTotal = 50m,
            LineTotal = 500m
        };
        item.OrderItemModifiers.Add(new OrderItemModifier
        {
            ProductId = 11,
            ModifierName = "Extra Cheese",
            AdditionalPrice = 50m
        });
        order.OrderItems.Add(item);
        order.Payments.Add(new Payment { PaymentMethod = "CASH", Amount = 200m, AmountTendered = 200m, ChangeAmount = 0m, PaymentStatus = PaymentStatuses.Paid, ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow });
        order.Payments.Add(new Payment { PaymentMethod = "GCASH", Amount = 300m, AmountTendered = 300m, ChangeAmount = 0m, PaymentStatus = PaymentStatuses.Paid, ReferenceNumber = "GC-999", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow });
        db.Orders.Add(order);
        await db.SaveChangesAsync();

        var ctrl = CreateReturnsControllerForVoidTest(db, "manager1", "Manager", 1, 1);

        // 1. Cashier PIN must be rejected
        var cashierAttempt = await ctrl.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = order.OrderId,
            VoidReason = "Cashier attempt",
            ApprovedByPinOrUsername = "1111"
        });
        Assert(cashierAttempt.Result is BadRequestObjectResult, "Cashier PIN must not be allowed to approve online void.");

        // 2. Username-as-PIN bypass must be rejected
        var usernameBypass = await ctrl.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = order.OrderId,
            VoidReason = "Username bypass",
            ApprovedByPinOrUsername = "manager1"
        });
        Assert(usernameBypass.Result is BadRequestObjectResult, "Username-as-PIN bypass must be rejected for online void.");

        // 3. Valid supervisor PIN succeeds
        var okAction = await ctrl.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = order.OrderId,
            VoidReason = "Customer cancelled order",
            ApprovedByPinOrUsername = "9999"
        });
        var okObj = okAction.Result as OkObjectResult;
        Assert(okObj != null, "Valid supervisor PIN must succeed for online void.");
        var dto = okObj!.Value as VoidResponseDto;
        Assert(dto != null && dto.CashRefundAmount == 200m && dto.NonCashPendingManualRefundAmount == 300m && !dto.IsOfflinePendingSync,
            $"Online void must separate Cash (200) vs Non-Cash pending manual refund (300); got Cash={dto?.CashRefundAmount}, NonCash={dto?.NonCashPendingManualRefundAmount}.");

        // Verify Refunds, CashDrawerShift, and BranchInventories
        var refunds = await db.Refunds.AsNoTracking().Where(r => r.VoidId == dto!.VoidId).ToListAsync();
        Assert(refunds.Count == 2, $"Expected 2 refund rows, got {refunds.Count}.");
        Assert(refunds.Single(r => r.RefundMethod == "CASH").Status == "COMPLETED", "Cash refund must be COMPLETED.");
        Assert(refunds.Single(r => r.RefundMethod == "GCASH").Status == "PENDING_MANUAL_REFUND", "GCash refund must be PENDING_MANUAL_REFUND.");

        var updatedShift = await db.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == shift.ShiftId);
        Assert(updatedShift.TotalCashRefunds == 200m && updatedShift.ExpectedBalance == 1000m,
            $"Only CASH portion (200) must increment TotalCashRefunds; got Refunds={updatedShift.TotalCashRefunds}, Expected={updatedShift.ExpectedBalance}.");

        var invMain = await db.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 10);
        var invMod = await db.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 11);
        Assert(invMain.StockQuantity == 10 && invMod.StockQuantity == 20,
            $"BranchInventories must restore main (8+2=10) and modifier (18+2=20); got Main={invMain.StockQuantity}, Mod={invMod.StockQuantity}.");
        int restockLogs = await db.StockAdjustments.CountAsync(a => a.AdjustmentType == "VOID_RESTOCK");
        Assert(restockLogs == 2, $"Expected 2 VOID_RESTOCK audit logs, got {restockLogs}.");

        // 4. Duplicate void rejected
        var dupAttempt = await ctrl.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = order.OrderId,
            VoidReason = "Duplicate",
            ApprovedByPinOrUsername = "9999"
        });
        Assert(dupAttempt.Result is BadRequestObjectResult, "Already-voided order must reject duplicate online void.");
    }

    private static async Task TestVoidOff02_EndToEndOfflineVoidRestartReconnectSyncAndIdempotency()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_void_off02_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Seed server catalog + inventory + invoice sequence + shift
            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Meals", IsActive = true });
            apiDb.Products.Add(new Product { ProductId = 10, CategoryId = 1, Name = "Chicken Meal", Price = 150m, IsActive = true, ProductType = "Food" });
            apiDb.BranchInventories.Add(new BranchInventory { BranchId = 1, ProductId = 10, StockQuantity = 25, LastUpdated = DateTime.UtcNow });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1, SeriesCode = "S1", Prefix = "INV01-",
                StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 7, IsActive = true
            });
            var srvShift = await apiDb.CashDrawerShifts.FirstAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
            srvShift.OpeningFloat = 1000m;
            srvShift.TotalCashSales = 0m;
            srvShift.TotalCashRefunds = 0m;
            srvShift.ClientShiftId = "SHIFT-OFF-01";
            CashDrawerController.RecalculateShiftExpectedBalance(srvShift);
            await apiDb.SaveChangesAsync();

            // Seed local SQLite and verify migration V4 created LocalTransactionVoids
            string localOrderId = Guid.NewGuid().ToString();
            using (var localDb = new LocalPosDbContext())
            {
                localDb.InitializeDatabase();
                Assert(LocalDatabaseMigrator.GetUserVersion(localDb.Database.GetDbConnection()) == LocalDatabaseMigrator.CurrentSchemaVersion,
                    "SQLite database must migrate cleanly to CurrentSchemaVersion (4).");

                await SeedLocalTerminalAndUsersAsync(localDb, shiftId: srvShift.ShiftId, clientShiftId: "SHIFT-OFF-01", openingFloat: 1000m);

                // Record an offline cash sale of 2x Chicken Meal = 300.00
                var nowLocal = BusinessDayClock.DeviceNow();
                localDb.Orders.Add(new LocalOrder
                {
                    OrderId = localOrderId,
                    OrderNumber = "POS01-000001",
                    InvoiceNumber = "INV01-0000001",
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    ShiftId = srvShift.ShiftId,
                    ClientShiftId = "SHIFT-OFF-01",
                    CashierId = "cashier1",
                    Subtotal = 300m,
                    TotalAmount = 300m,
                    PaymentStatus = PaymentStatuses.Paid,
                    OrderStatus = "COMPLETED",
                    CreatedAt = nowLocal
                });
                localDb.OrderItems.Add(new LocalOrderItem
                {
                    OrderId = localOrderId,
                    ProductId = 10,
                    ProductName = "Chicken Meal",
                    Quantity = 2m,
                    UnitPrice = 150m,
                    LineTotal = 300m
                });
                localDb.Payments.Add(new LocalPayment
                {
                    OrderId = localOrderId,
                    PaymentMethod = "CASH",
                    Amount = 300m,
                    TenderedAmount = 500m,
                    ChangeAmount = 200m,
                    PaymentStatus = PaymentStatuses.Paid,
                    PaidAt = nowLocal
                });
                var localShift = await localDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == srvShift.ShiftId);
                localShift.TotalCashSales = 300m;
                localShift.ExpectedBalance = 1300m;

                localDb.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "Order",
                    EntityId = localOrderId,
                    PayloadJson = "{}",
                    Status = "PENDING",
                    CreatedAt = BusinessDayClock.UtcNow().AddSeconds(-10)
                });
                await localDb.SaveChangesAsync();
            }

            // 1. Perform Offline Void authorized by Manager PIN 9999
            var session = CreateOfflineVoidSession(username: "cashier1", role: "Cashier");
            var voidService = new OfflineVoidService(session);
            var voidRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = localOrderId,
                InvoiceNumber = "INV01-0000001",
                OrderNumber = "POS01-000001",
                VoidReason = "Wrong item rung up while offline",
                SupervisorPin = "9999"
            });

            Assert(voidRes.Success && voidRes.Response != null && voidRes.Response.IsOfflinePendingSync,
                $"Offline void must succeed with valid supervisor PIN; error='{voidRes.ErrorMessage}'.");
            Assert(voidRes.Response!.CashRefundAmount == 300m && voidRes.Response.NonCashPendingManualRefundAmount == 0m,
                "Cash sale offline void must record full 300 cash refund.");
            string clientVoidId = voidRes.Response.ClientVoidId!;

            // 2. Simulate POS.Client restart while still offline -> verify atomic persistence & zero plaintext PIN storage
            LocalPosDbContext.ResetInitializationState(dbPath);
            using (var verifyDb = new LocalPosDbContext())
            {
                verifyDb.InitializeDatabase();
                var lo = await verifyDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == localOrderId);
                var lp = await verifyDb.Payments.AsNoTracking().SingleAsync(p => p.OrderId == localOrderId);
                var ls = await verifyDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId);
                var lv = await verifyDb.TransactionVoids.AsNoTracking().SingleAsync(v => v.ClientVoidId == clientVoidId);
                var ob = await verifyDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "VOID" && o.EntityId == clientVoidId);
                var audit = await verifyDb.SyncAuditLogs.AsNoTracking().FirstOrDefaultAsync(a => a.Message.Contains("[OFFLINE_VOID]") && a.Message.Contains(clientVoidId));

                Assert(lo.OrderStatus == "VOIDED" && lo.PaymentStatus == "VOIDED", "LocalOrder must remain VOIDED after restart.");
                Assert(lp.PaymentStatus == "REFUNDED", "LocalPayment must remain REFUNDED after restart.");
                Assert(ls.TotalCashRefunds == 300m && ls.ExpectedBalance == 1000m,
                    $"Local shift balance must reflect cash refund (Expected=1000); got Refunds={ls.TotalCashRefunds}, Expected={ls.ExpectedBalance}.");
                Assert(lv.SyncStatus == "PENDING" && lv.ApprovedBy == "manager1" && lv.RequestedBy == "cashier1",
                    "LocalTransactionVoid audit fields must persist across restart.");
                Assert(!ob.PayloadJson.Contains("\"9999\"") && !lv.SupervisorPinHash.Equals("9999"),
                    "Plaintext supervisor PIN must NEVER be persisted in SQLite or outbox payload.");
                Assert(audit != null, "LocalSyncAuditLog must record the offline void event.");
            }

            // 3. Reconnect & synchronize via OfflineTransactionSyncService
            var syncCtrl = CreateOfflineSyncControllerForVoidTest(apiDb, "manager1", "Manager", 1, 1);
            using var httpClient = new HttpClient(new VoidSyncRoutingHandler(syncCtrl)) { BaseAddress = new Uri("http://localhost/") };
            using var syncDb = new LocalPosDbContext();
            var syncService = new OfflineTransactionSyncService(syncDb);

            var pending = await syncService.GetPendingOutboxItemsAsync();
            Assert(pending.Count == 2 && pending[0].EntityType == "Order" && pending[1].EntityType == "VOID",
                $"Outbox priority must upload Order BEFORE VOID; got [{string.Join(", ", pending.Select(p => p.EntityType))}].");

            // Test out-of-order guard: attempting to upload VOID before Order is SYNCED must defer without failing permanently
            bool prematureVoidUpload = await syncService.UploadTransactionAsync(pending[1], httpClient);
            Assert(!prematureVoidUpload, "VOID upload must defer while parent Order outbox item is still PENDING.");

            // Upload Order first, then VOID
            bool orderUploaded = await syncService.UploadTransactionAsync(pending[0], httpClient);
            Assert(orderUploaded, "Original offline Order must upload cleanly.");

            var nextPending = await syncService.GetPendingOutboxItemsAsync();
            // Reset NextRetryAt to immediately test void upload after parent order synced
            using (var fixRetryDb = new LocalPosDbContext())
            {
                var voidOb = await fixRetryDb.SyncOutboxes.SingleAsync(o => o.EntityType == "VOID" && o.EntityId == clientVoidId);
                Assert(voidOb.Status == "RETRY_PENDING" && voidOb.RetryCount == 0,
                    $"Deferred VOID must stay RETRY_PENDING with RetryCount=0; got Status={voidOb.Status}, Retry={voidOb.RetryCount}.");
                voidOb.NextRetryAt = null;
                await fixRetryDb.SaveChangesAsync();
            }

            nextPending = await syncService.GetPendingOutboxItemsAsync();
            Assert(nextPending.Count == 1 && nextPending[0].EntityType == "VOID", "VOID must now be ready for upload.");
            bool voidUploaded = await syncService.UploadTransactionAsync(nextPending[0], httpClient);
            Assert(voidUploaded, "Offline VOID must synchronize cleanly to server.");

            // 4. Verify PostgreSQL state (Order VOIDED, TransactionVoid, Refund COMPLETED, CashDrawer balanced, Inventory restored back to 25!)
            var srvOrder = await apiDb.Orders.AsNoTracking().Include(o => o.Payments).SingleAsync(o => o.InvoiceNumber == "INV01-0000001");
            Assert(srvOrder.OrderStatus == "VOIDED" && srvOrder.PaymentStatus == "VOIDED", "Server Order must be VOIDED.");
            Assert(srvOrder.Payments.Single().PaymentStatus == "REFUNDED", "Server Payment must be REFUNDED.");

            var srvVoid = await apiDb.TransactionVoids.AsNoTracking().SingleAsync(v => v.ClientVoidId == clientVoidId);
            Assert(srvVoid.ApprovedBy == "manager1" && srvVoid.RequestedBy == "cashier1" && srvVoid.Status == "APPROVED",
                "Server TransactionVoid must preserve audit trail.");

            var srvRefund = await apiDb.Refunds.AsNoTracking().SingleAsync(r => r.VoidId == srvVoid.VoidId);
            Assert(srvRefund.RefundMethod == "CASH" && srvRefund.RefundAmount == 300m && srvRefund.Status == "COMPLETED",
                "Server Refund must be COMPLETED for cash void.");

            var srvShiftAfter = await apiDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId);
            Assert(srvShiftAfter.TotalCashSales == 300m && srvShiftAfter.TotalCashRefunds == 300m && srvShiftAfter.ExpectedBalance == 1000m,
                $"Server CashDrawerShift must net to 1000 (Sales=300, Refunds=300); got Sales={srvShiftAfter.TotalCashSales}, Refunds={srvShiftAfter.TotalCashRefunds}, Expected={srvShiftAfter.ExpectedBalance}.");

            var srvInv = await apiDb.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 10);
            Assert(srvInv.StockQuantity == 25, $"Server stock must return to 25 (25 - 2 sale + 2 void restock); got {srvInv.StockQuantity}.");

            using (var postSyncLocal = new LocalPosDbContext())
            {
                var lvSynced = await postSyncLocal.TransactionVoids.AsNoTracking().SingleAsync(v => v.ClientVoidId == clientVoidId);
                Assert(lvSynced.SyncStatus == "SYNCED" && lvSynced.ServerVoidId == srvVoid.VoidId,
                    "LocalTransactionVoid must be marked SYNCED with ServerVoidId.");
            }

            // 5. Idempotent replay of exact same OfflineVoidSyncRequest -> must return 200 OK with ZERO double-refund or double-restock!
            string rawPayload;
            using (var pDb = new LocalPosDbContext())
            {
                rawPayload = (await pDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "VOID" && o.EntityId == clientVoidId)).PayloadJson;
            }
            var replayReq = JsonSerializer.Deserialize<OfflineVoidSyncRequest>(rawPayload)!;
            var replayRes = await syncCtrl.SyncVoid(replayReq);
            Assert(replayRes.Result is OkObjectResult, "Idempotent replay of offline void must return 200 OK.");
            Assert(await apiDb.TransactionVoids.CountAsync(v => v.ClientVoidId == clientVoidId) == 1, "No duplicate TransactionVoid rows allowed.");
            Assert((await apiDb.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 10)).StockQuantity == 25,
                "Idempotent replay must NOT double-restock inventory.");
            Assert((await apiDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId)).TotalCashRefunds == 300m,
                "Idempotent replay must NOT double-count cash refunds.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestVoidOff03_SecurityMatrixAndServerConflictPreservation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_void_off03_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            string orderId = Guid.NewGuid().ToString();
            string otherTerminalOrderId = Guid.NewGuid().ToString();

            // Case 1: NO authorized supervisor cached on terminal -> exact required message!
            using (var db = new LocalPosDbContext())
            {
                db.InitializeDatabase();
                await SeedLocalTerminalAndUsersAsync(db, includeSupervisor: false);
                db.Orders.AddRange(
                    new LocalOrder
                    {
                        OrderId = orderId, OrderNumber = "POS01-000001", InvoiceNumber = "INV01-0000001",
                        BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1",
                        Subtotal = 100m, TotalAmount = 100m, OrderStatus = "COMPLETED", PaymentStatus = PaymentStatuses.Paid, CreatedAt = BusinessDayClock.DeviceNow()
                    },
                    new LocalOrder
                    {
                        OrderId = otherTerminalOrderId, OrderNumber = "POS02-000001", InvoiceNumber = "INV02-0000001",
                        BranchId = 1, OutletId = 1, TerminalId = 2, CashierId = "cashier2",
                        Subtotal = 100m, TotalAmount = 100m, OrderStatus = "COMPLETED", PaymentStatus = PaymentStatuses.Paid, CreatedAt = BusinessDayClock.DeviceNow()
                    });
                db.Payments.Add(new LocalPayment { OrderId = orderId, PaymentMethod = "CASH", Amount = 100m, PaymentStatus = PaymentStatuses.Paid, PaidAt = BusinessDayClock.DeviceNow() });
                await db.SaveChangesAsync();
            }

            var session = CreateOfflineVoidSession(username: "cashier1", role: "Cashier");
            var voidService = new OfflineVoidService(session);

            var noSupRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, VoidReason = "No supervisor test", SupervisorPin = "9999"
            });
            Assert(!noSupRes.Success && noSupRes.ErrorMessage == VoidAuthorizationHelper.NoAuthorizedSupervisorMessage,
                $"Missing supervisor must return exact required message, got '{noSupRes.ErrorMessage}'.");

            // Case 2: Add expired supervisor (35 days old) -> must be rejected!
            using (var db = new LocalPosDbContext())
            {
                db.Cashiers.Add(new LocalCashier
                {
                    UserId = 2, Username = "manager1", Role = "Manager",
                    PinHash = VoidAuthorizationHelper.ComputePinHash("9999"),
                    SyncedAtUtc = BusinessDayClock.UtcNow().AddDays(-35)
                });
                await db.SaveChangesAsync();
            }
            var expiredRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, VoidReason = "Expired cred test", SupervisorPin = "9999"
            });
            Assert(!expiredRes.Success && expiredRes.ErrorMessage!.Contains("expired", StringComparison.OrdinalIgnoreCase),
                $"Expired supervisor credentials must be rejected; got '{expiredRes.ErrorMessage}'.");

            // Refresh supervisor credential timestamp
            using (var db = new LocalPosDbContext())
            {
                var mgr = await db.Cashiers.SingleAsync(c => c.UserId == 2);
                mgr.SyncedAtUtc = BusinessDayClock.UtcNow();
                await db.SaveChangesAsync();
            }

            // Case 3: Invalid PIN rejected
            var badPinRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, VoidReason = "Wrong pin", SupervisorPin = "0000"
            });
            Assert(!badPinRes.Success, "Invalid supervisor PIN must be rejected.");

            // Case 4: Cashier self-approval (enters own PIN 1111) rejected
            var selfApproveRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, VoidReason = "Cashier self-approve", SupervisorPin = "1111"
            });
            Assert(!selfApproveRes.Success && selfApproveRes.ErrorMessage!.Contains("does not have authorization", StringComparison.OrdinalIgnoreCase),
                $"Cashier self-approval must be denied; got '{selfApproveRes.ErrorMessage}'.");

            // Case 5: Cross-terminal order rejected
            var crossTermRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = otherTerminalOrderId, VoidReason = "Cross terminal", SupervisorPin = "9999"
            });
            Assert(!crossTermRes.Success && crossTermRes.ErrorMessage!.Contains("active branch and terminal", StringComparison.OrdinalIgnoreCase),
                $"Cross-terminal void must be denied; got '{crossTermRes.ErrorMessage}'.");

            // Case 6: Legitimate offline void succeeds, then duplicate attempt is rejected
            var okRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, InvoiceNumber = "INV01-0000001", VoidReason = "Valid offline void", SupervisorPin = "9999"
            });
            Assert(okRes.Success && okRes.Response != null, "Valid offline void should succeed.");
            string clientVoidId = okRes.Response!.ClientVoidId!;

            var dupLocalRes = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                LocalOrderId = orderId, InvoiceNumber = "INV01-0000001", VoidReason = "Duplicate local", SupervisorPin = "9999"
            });
            Assert(!dupLocalRes.Success && dupLocalRes.ErrorMessage!.Contains("already voided", StringComparison.OrdinalIgnoreCase),
                $"Duplicate offline void must be rejected; got '{dupLocalRes.ErrorMessage}'.");

            // Case 7: Server conflict handling (tampered proof hash AND revoked/demoted supervisor on server)
            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Tampered AuthorizationProofHash -> 409 Conflict directly from server
            string payloadJson;
            using (var rDb = new LocalPosDbContext())
            {
                payloadJson = (await rDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "VOID" && o.EntityId == clientVoidId)).PayloadJson;
            }
            var tamperedReq = JsonSerializer.Deserialize<OfflineVoidSyncRequest>(payloadJson)!;
            tamperedReq.AuthorizationProofHash = "DEADBEEF00000000000000000000000000000000000000000000000000000000";
            var syncCtrl = CreateOfflineSyncControllerForVoidTest(apiDb, "manager1", "Manager", 1, 1);
            var tamperRes = await syncCtrl.SyncVoid(tamperedReq);
            Assert(tamperRes.Result is ConflictObjectResult, "Tampered AuthorizationProofHash must return 409 Conflict.");

            // Now demote manager1 to Cashier on server BEFORE sync -> OfflineTransactionSyncService must flag CONFLICT/NEEDS_ATTENTION & preserve local void/audit trail!
            var serverMgr = await apiDb.Users.SingleAsync(u => u.Username == "manager1");
            serverMgr.Role = "Cashier";
            await apiDb.SaveChangesAsync();

            using var httpClient = new HttpClient(new VoidSyncRoutingHandler(syncCtrl)) { BaseAddress = new Uri("http://localhost/") };
            using var syncDb = new LocalPosDbContext();
            var syncService = new OfflineTransactionSyncService(syncDb);
            var voidOutbox = await syncDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "VOID" && o.EntityId == clientVoidId);
            bool uploaded = await syncService.UploadTransactionAsync(voidOutbox, httpClient);
            Assert(!uploaded, "Revoked/demoted supervisor void sync must fail with conflict.");

            using (var checkDb = new LocalPosDbContext())
            {
                var lv = await checkDb.TransactionVoids.AsNoTracking().SingleAsync(v => v.ClientVoidId == clientVoidId);
                var ob = await checkDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityId == clientVoidId);
                var lo = await checkDb.Orders.AsNoTracking().SingleAsync(o => o.OrderId == orderId);

                Assert(lv.SyncStatus == "CONFLICT" && !string.IsNullOrWhiteSpace(lv.SyncError),
                    $"Local void must be flagged CONFLICT with error preserved; got SyncStatus={lv.SyncStatus}.");
                Assert(ob.Status == "NEEDS_ATTENTION", $"Outbox must be flagged NEEDS_ATTENTION; got {ob.Status}.");
                Assert(lo.OrderStatus == "VOIDED", "Conflict must NEVER silently un-void the local order or discard audit trail.");
            }
        }
        finally
        {
            BusinessDayClock.ResetProviders();
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static async Task TestVoidOff04_PaymentMethodMatrixSplitVoidAndOverlay()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_void_off04_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Seed server products (main + linked modifier) and inventory + invoice sequence
            apiDb.Categories.Add(new Category { CategoryId = 1, Name = "Drinks", IsActive = true });
            apiDb.Products.AddRange(
                new Product { ProductId = 20, CategoryId = 1, Name = "Iced Coffee", Price = 150m, IsActive = true, ProductType = "Drink" },
                new Product { ProductId = 21, CategoryId = 1, Name = "Oat Milk", Price = 50m, IsActive = true, ProductType = "Addon" });
            apiDb.BranchInventories.AddRange(
                new BranchInventory { BranchId = 1, ProductId = 20, StockQuantity = 50, LastUpdated = DateTime.UtcNow },
                new BranchInventory { BranchId = 1, ProductId = 21, StockQuantity = 30, LastUpdated = DateTime.UtcNow });
            apiDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1, SeriesCode = "S1", Prefix = "INV01-",
                StartingNumber = 1, EndingNumber = 9999, LastIssuedNumber = 0, PaddingLength = 7, IsActive = true
            });
            var srvShift = await apiDb.CashDrawerShifts.FirstAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
            srvShift.OpeningFloat = 1000m;
            srvShift.TotalCashSales = 0m;
            srvShift.TotalCashRefunds = 0m;
            srvShift.ClientShiftId = "SHIFT-PAY-01";
            CashDrawerController.RecalculateShiftExpectedBalance(srvShift);
            await apiDb.SaveChangesAsync();

            string cardOrderId = Guid.NewGuid().ToString();
            string gcashOrderId = Guid.NewGuid().ToString();
            string splitOrderId = Guid.NewGuid().ToString();

            using (var localDb = new LocalPosDbContext())
            {
                localDb.InitializeDatabase();
                await SeedLocalTerminalAndUsersAsync(localDb, shiftId: srvShift.ShiftId, clientShiftId: "SHIFT-PAY-01", openingFloat: 1000m);
                var nowLocal = BusinessDayClock.DeviceNow();

                // 1. Card-only order (400)
                localDb.Orders.Add(new LocalOrder { OrderId = cardOrderId, OrderNumber = "POS01-000001", InvoiceNumber = "INV01-0000001", BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = srvShift.ShiftId, ClientShiftId = "SHIFT-PAY-01", CashierId = "cashier1", Subtotal = 400m, TotalAmount = 400m, OrderStatus = "COMPLETED", PaymentStatus = PaymentStatuses.Paid, CreatedAt = nowLocal });
                localDb.Payments.Add(new LocalPayment { OrderId = cardOrderId, PaymentMethod = "CARD", Amount = 400m, PaymentStatus = PaymentStatuses.Paid, ReferenceNumber = "VISA-1234", PaidAt = nowLocal });

                // 2. GCash-only order (250)
                localDb.Orders.Add(new LocalOrder { OrderId = gcashOrderId, OrderNumber = "POS01-000002", InvoiceNumber = "INV01-0000002", BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = srvShift.ShiftId, ClientShiftId = "SHIFT-PAY-01", CashierId = "cashier1", Subtotal = 250m, TotalAmount = 250m, OrderStatus = "COMPLETED", PaymentStatus = PaymentStatuses.Paid, CreatedAt = nowLocal });
                localDb.Payments.Add(new LocalPayment { OrderId = gcashOrderId, PaymentMethod = "GCASH", Amount = 250m, PaymentStatus = PaymentStatuses.Paid, ReferenceNumber = "GC-5555", PaidAt = nowLocal });

                // 3. Split CASH (200) + GCASH (200) order with linked modifier product (2x Iced Coffee + Oat Milk)
                localDb.Orders.Add(new LocalOrder { OrderId = splitOrderId, OrderNumber = "POS01-000003", InvoiceNumber = "INV01-0000003", BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = srvShift.ShiftId, ClientShiftId = "SHIFT-PAY-01", CashierId = "cashier1", Subtotal = 400m, TotalAmount = 400m, OrderStatus = "COMPLETED", PaymentStatus = PaymentStatuses.Paid, CreatedAt = nowLocal });
                localDb.OrderItems.Add(new LocalOrderItem
                {
                    OrderId = splitOrderId,
                    ProductId = 20,
                    ProductName = "Iced Coffee",
                    Quantity = 2m,
                    UnitPrice = 200m,
                    LineTotal = 400m,
                    ModifiersJson = JsonSerializer.Serialize(new List<OrderItemModifierCreateDto>
                    {
                        new() { ModifierOptionId = 1, ProductId = 21, ModifierName = "Oat Milk", AdditionalPrice = 50m }
                    })
                });
                localDb.Payments.AddRange(
                    new LocalPayment { OrderId = splitOrderId, PaymentMethod = "CASH", Amount = 200m, TenderedAmount = 200m, ChangeAmount = 0m, PaymentStatus = PaymentStatuses.Paid, PaidAt = nowLocal },
                    new LocalPayment { OrderId = splitOrderId, PaymentMethod = "GCASH", Amount = 200m, TenderedAmount = 200m, ChangeAmount = 0m, PaymentStatus = PaymentStatuses.Paid, ReferenceNumber = "GC-7777", PaidAt = nowLocal });

                var ls = await localDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == srvShift.ShiftId);
                ls.TotalCashSales = 200m;
                ls.ExpectedBalance = 1200m;

                localDb.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "Order", EntityId = splitOrderId, PayloadJson = "{}", Status = "PENDING", CreatedAt = BusinessDayClock.UtcNow().AddSeconds(-10)
                });
                await localDb.SaveChangesAsync();
            }

            var session = CreateOfflineVoidSession(username: "manager1", role: "Manager");
            var voidService = new OfflineVoidService(session);

            // Void Card-only -> ZERO impact on cash drawer!
            var cardVoid = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest { LocalOrderId = cardOrderId, VoidReason = "Card void", SupervisorPin = "9999" });
            Assert(cardVoid.Success && cardVoid.Response!.CashRefundAmount == 0m && cardVoid.Response.NonCashPendingManualRefundAmount == 400m,
                "Card-only void must not affect cash drawer and must flag 400 as PENDING_MANUAL_REFUND.");

            // Void GCash-only -> ZERO impact on cash drawer!
            var gcashVoid = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest { LocalOrderId = gcashOrderId, VoidReason = "GCash void", SupervisorPin = "9999" });
            Assert(gcashVoid.Success && gcashVoid.Response!.CashRefundAmount == 0m && gcashVoid.Response.NonCashPendingManualRefundAmount == 250m,
                "GCash-only void must not affect cash drawer and must flag 250 as PENDING_MANUAL_REFUND.");

            using (var checkShiftDb = new LocalPosDbContext())
            {
                var ls = await checkShiftDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId);
                Assert(ls.TotalCashRefunds == 0m && ls.ExpectedBalance == 1200m,
                    $"Non-cash voids must NEVER alter TotalCashRefunds; got Refunds={ls.TotalCashRefunds}, Expected={ls.ExpectedBalance}.");
            }

            // Void Split CASH (200) + GCASH (200) -> ONLY 200 cash impacts cash drawer!
            var splitVoid = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest { LocalOrderId = splitOrderId, VoidReason = "Split void", SupervisorPin = "9999" });
            Assert(splitVoid.Success && splitVoid.Response!.CashRefundAmount == 200m && splitVoid.Response.NonCashPendingManualRefundAmount == 200m,
                "Split void must separate 200 Cash vs 200 GCash pending manual refund.");

            using (var checkShiftDb = new LocalPosDbContext())
            {
                var ls = await checkShiftDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId);
                Assert(ls.TotalCashRefunds == 200m && ls.ExpectedBalance == 1000m,
                    $"Split void must adjust cash drawer by ONLY 200; got Refunds={ls.TotalCashRefunds}, Expected={ls.ExpectedBalance}.");
            }

            // Sync split order + split void to server and verify Refunds + Shift + Main & Modifier Inventory restoration
            var syncCtrl = CreateOfflineSyncControllerForVoidTest(apiDb, "manager1", "Manager", 1, 1);
            using var httpClient = new HttpClient(new VoidSyncRoutingHandler(syncCtrl)) { BaseAddress = new Uri("http://localhost/") };
            using var syncDb = new LocalPosDbContext();
            var syncService = new OfflineTransactionSyncService(syncDb);

            var orderOb = await syncDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "Order" && o.EntityId == splitOrderId);
            var splitVoidOb = await syncDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "VOID" && o.EntityId == splitVoid.Response!.ClientVoidId);
            Assert(await syncService.UploadTransactionAsync(orderOb, httpClient), "Split order sync must succeed.");
            Assert(await syncService.UploadTransactionAsync(splitVoidOb, httpClient), "Split void sync must succeed.");

            var srvVoid = await apiDb.TransactionVoids.AsNoTracking().SingleAsync(v => v.ClientVoidId == splitVoid.Response!.ClientVoidId);
            var srvRefunds = await apiDb.Refunds.AsNoTracking().Where(r => r.VoidId == srvVoid.VoidId).ToListAsync();
            Assert(srvRefunds.Count == 2, $"Expected 2 server refund rows for split void, got {srvRefunds.Count}.");
            Assert(srvRefunds.Single(r => r.RefundMethod == "CASH").Status == "COMPLETED", "Server CASH refund must be COMPLETED.");
            Assert(srvRefunds.Single(r => r.RefundMethod == "GCASH").Status == "PENDING_MANUAL_REFUND", "Server GCASH refund must be PENDING_MANUAL_REFUND.");

            var srvShiftAfter = await apiDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == srvShift.ShiftId);
            Assert(srvShiftAfter.TotalCashSales == 200m && srvShiftAfter.TotalCashRefunds == 200m && srvShiftAfter.ExpectedBalance == 1000m,
                $"Server shift must reflect ONLY cash portion (200); got Refunds={srvShiftAfter.TotalCashRefunds}, Expected={srvShiftAfter.ExpectedBalance}.");

            var invCoffee = await apiDb.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 20);
            var invOat = await apiDb.BranchInventories.AsNoTracking().SingleAsync(i => i.BranchId == 1 && i.ProductId == 21);
            Assert(invCoffee.StockQuantity == 50 && invOat.StockQuantity == 30,
                $"Both main product (50) and linked modifier (30) inventories must be restored after split void sync; got Coffee={invCoffee.StockQuantity}, Oat={invOat.StockQuantity}.");

            // Verify online-originated order voided offline via snapshot + ApplyLocalVoidOverlayAsync
            var onlineSnapshot = new OrderResponseDto
            {
                OrderId = 777,
                OrderNumber = "POS01-000777",
                InvoiceNumber = "INV01-0000777",
                BranchId = 1,
                TerminalId = 1,
                OrderStatus = "COMPLETED",
                PaymentStatus = PaymentStatuses.Paid,
                TotalAmount = 150m,
                Payments = new() { new PaymentResponseDto { PaymentMethod = "CASH", Amount = 150m, PaymentStatus = PaymentStatuses.Paid } }
            };
            var snapVoid = await voidService.ProcessOfflineVoidAsync(new OfflineVoidRequest
            {
                ServerOrderId = 777,
                InvoiceNumber = "INV01-0000777",
                OrderNumber = "POS01-000777",
                VoidReason = "Online order voided during outage",
                SupervisorPin = "9999",
                OrderSnapshot = onlineSnapshot
            });
            Assert(snapVoid.Success, "Voiding cached online order via snapshot while offline must succeed.");

            var txList = new List<OrderResponseDto> { onlineSnapshot };
            var clientTxService = new ClientTransactionService(httpClient, session);
            await clientTxService.ApplyLocalVoidOverlayAsync(txList);
            Assert(txList[0].OrderStatus == "VOIDED" && txList[0].PaymentStatus == "VOIDED",
                "ApplyLocalVoidOverlayAsync must mark pending offline-voided online transactions as VOIDED immediately.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", prevDbPath);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
