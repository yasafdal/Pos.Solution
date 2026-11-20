using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public partial class Program
{
    private static HeldSalesController CreateHeldSalesControllerForTest(PosDbContext db, string username = "cashier1", string role = "Cashier", int? branchId = 1, int? terminalId = 1)
    {
        return new HeldSalesController(db)
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

    private static async Task TestHeldExp01_Boundary359Vs400AmExpirationIdempotencyAndIsolation()
    {
        try
        {
            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Add Outlet 2 + Terminal 2 in Branch 1 to verify multi-terminal / multi-outlet isolation
            db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 1, OutletCode = "OUT-2", Name = "Outlet 2", IsActive = true });
            db.Terminals.Add(new Terminal { TerminalId = 2, BranchId = 1, OutletId = 2, TerminalCode = "POS-2", Name = "Counter 2", IsActive = true });
            await db.SaveChangesAsync();

            // 1. Set clock to Nov 19, 2026 03:59 AM Asia/Manila (2026-11-18 19:59:00 UTC) -> Business Date: 2026-11-18
            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 18, 19, 59, 0, DateTimeKind.Utc);
            Assert(BusinessDayClock.GetCurrentBusinessDate() == new DateOnly(2026, 11, 18),
                $"Nov 19 03:59 AM Manila must map to Business Date 2026-11-18, got {BusinessDayClock.GetCurrentBusinessDate()}.");

            var ctrlT1 = CreateHeldSalesControllerForTest(db, "cashier1", "Cashier", branchId: 1, terminalId: 1);
            var createPrevRes = await ctrlT1.CreateHeldSale(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1",
                CustomerName = "Late Night Guest", Notes = "No onions",
                Subtotal = 250m, TotalAmount = 250m,
                Items = new()
                {
                    new HeldSaleItemDto
                    {
                        ProductId = 1, ProductName = "Sisig Platter", Quantity = 1, BasePrice = 250m, LineTotal = 250m,
                        SpecialInstructions = "Extra calamansi",
                        Modifiers = new() { new HeldSaleModifierDto { ModifierOptionId = 10, ModifierName = "Extra Egg", AdditionalPrice = 25m } }
                    }
                }
            });
            var prevHeld = ((OkObjectResult)createPrevRes.Result!).Value as HeldSaleDto;
            Assert(prevHeld != null && prevHeld.BusinessDate == "2026-11-18",
                $"Held Sale created at 03:59 AM must have BusinessDate='2026-11-18', got '{prevHeld?.BusinessDate}'.");

            // Seed older previous-day HELD (Nov 17), COMPLETED (Nov 18), and manually CANCELLED (Nov 18) records
            var manualCancelTime = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
            db.HeldSales.AddRange(
                new HeldSale
                {
                    HeldSaleId = 9001, BranchId = 1, TerminalId = 1, CashierId = "cashier1",
                    HoldReference = "HS-OLD-17", CustomerName = "Two Days Ago",
                    Subtotal = 100m, TotalAmount = 100m, Status = "HELD", BusinessDate = "2026-11-17",
                    CreatedAt = new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc)
                },
                new HeldSale
                {
                    HeldSaleId = 9002, BranchId = 1, TerminalId = 1, CashierId = "cashier1",
                    HoldReference = "HS-DONE-18", CustomerName = "Already Completed",
                    Subtotal = 300m, TotalAmount = 300m, Status = "COMPLETED", BusinessDate = "2026-11-18",
                    CreatedAt = new DateTime(2026, 11, 18, 8, 0, 0, DateTimeKind.Utc)
                },
                new HeldSale
                {
                    HeldSaleId = 9003, BranchId = 1, TerminalId = 1, CashierId = "cashier1",
                    HoldReference = "HS-MANCANCEL-18", CustomerName = "Manually Cancelled",
                    Subtotal = 150m, TotalAmount = 150m, Status = "CANCELLED", BusinessDate = "2026-11-18",
                    CreatedAt = new DateTime(2026, 11, 18, 9, 0, 0, DateTimeKind.Utc),
                    CancelledAt = manualCancelTime,
                    CancellationReason = "Customer changed mind"
                });
            await db.SaveChangesAsync();

            // Verify at 03:59 AM that prevHeld IS visible in Held Sales
            var beforeCutoffList = (((OkObjectResult)(await ctrlT1.GetHeldSales(1, 1, default, 1)).Result!).Value as IEnumerable<HeldSaleDto>)!.ToList();
            Assert(beforeCutoffList.Count == 1 && beforeCutoffList[0].HeldSaleId == prevHeld!.HeldSaleId,
                $"At 03:59 AM, only 2026-11-18 Held Sale should be active (2026-11-17 auto-expired); got {beforeCutoffList.Count}.");

            // 2. Advance clock by 1 minute to Nov 19, 2026 04:00 AM Asia/Manila (2026-11-18 20:00:00 UTC) -> Business Date: 2026-11-19
            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 18, 20, 0, 0, DateTimeKind.Utc);
            Assert(BusinessDayClock.GetCurrentBusinessDate() == new DateOnly(2026, 11, 19),
                $"Nov 19 04:00 AM Manila must map to Business Date 2026-11-19, got {BusinessDayClock.GetCurrentBusinessDate()}.");

            // Create current-day Held Sales on Terminal 1 AND Terminal 2 (Outlet 2)
            var createTodayT1 = await ctrlT1.CreateHeldSale(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", CustomerName = "Morning Guest T1",
                Subtotal = 120m, TotalAmount = 120m,
                Items = new() { new HeldSaleItemDto { ProductId = 1, ProductName = "Coffee", Quantity = 1, BasePrice = 120m, LineTotal = 120m } }
            });
            var todayT1 = ((OkObjectResult)createTodayT1.Result!).Value as HeldSaleDto;
            Assert(todayT1 != null && todayT1.BusinessDate == "2026-11-19", "Today's Held Sale must have BusinessDate='2026-11-19'.");

            var ctrlT2 = CreateHeldSalesControllerForTest(db, "cashier2", "Cashier", branchId: 1, terminalId: 2);
            var createTodayT2 = await ctrlT2.CreateHeldSale(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 2, TerminalId = 2, CashierId = "cashier2", CustomerName = "Morning Guest T2",
                Subtotal = 200m, TotalAmount = 200m,
                Items = new() { new HeldSaleItemDto { ProductId = 1, ProductName = "Breakfast", Quantity = 1, BasePrice = 200m, LineTotal = 200m } }
            });
            var todayT2 = ((OkObjectResult)createTodayT2.Result!).Value as HeldSaleDto;
            Assert(todayT2 != null && todayT2.BusinessDate == "2026-11-19", "Terminal 2 Held Sale must have BusinessDate='2026-11-19'.");

            // 3. Open Held Sales on Terminal 1 -> must ONLY show todayT1 and auto-cancel prevHeld!
            var afterCutoffList = (((OkObjectResult)(await ctrlT1.GetHeldSales(1, 1, default, 1)).Result!).Value as IEnumerable<HeldSaleDto>)!.ToList();
            Assert(afterCutoffList.Count == 1 && afterCutoffList[0].HeldSaleId == todayT1!.HeldSaleId,
                $"At 04:00 AM, Terminal 1 must see ONLY today's Held Sale, got {afterCutoffList.Count}.");

            // Verify prevHeld was transitioned to CANCELLED with timestamp, reason, and full data preserved (NOT deleted)
            var dbPrev = await db.HeldSales.AsNoTracking()
                .Include(h => h.HeldSaleItems).ThenInclude(i => i.HeldSaleItemModifiers)
                .SingleAsync(h => h.HeldSaleId == prevHeld!.HeldSaleId);
            Assert(dbPrev.Status == "CANCELLED" && dbPrev.CancelledAt.HasValue &&
                   dbPrev.CancellationReason == HeldSalePolicies.PreviousBusinessDayCancellationReason,
                $"Previous-day Held Sale must be auto-cancelled with reason & timestamp (Status={dbPrev.Status}, Reason={dbPrev.CancellationReason}).");
            Assert(dbPrev.HeldSaleItems.Count == 1 &&
                   dbPrev.HeldSaleItems.First().SpecialInstructions == "Extra calamansi" &&
                   dbPrev.HeldSaleItems.First().HeldSaleItemModifiers.Count == 1,
                "Original items, special instructions, and modifiers must be preserved on auto-cancelled Held Sale.");
            Assert(await db.Orders.CountAsync() == 0, "Expired Held Sales must NEVER create financial Orders.");

            // Verify audit log count & idempotency across repeated calls
            int auditCount1 = await db.HeldSaleAuditLogs.CountAsync(a => a.HeldSaleId == prevHeld!.HeldSaleId && a.Action == "CANCELLED");
            Assert(auditCount1 == 1, $"Expected exactly 1 CANCELLED audit log for expired Held Sale, got {auditCount1}.");

            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 18, 20, 15, 0, DateTimeKind.Utc);
            await ctrlT1.GetHeldSales(1, 1, default, 1);
            await ctrlT1.GetHeldSales(1, 1, default, 1);

            int auditCount2 = await db.HeldSaleAuditLogs.CountAsync(a => a.HeldSaleId == prevHeld!.HeldSaleId && a.Action == "CANCELLED");
            var dbPrevAfterRepeat = await db.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == prevHeld!.HeldSaleId);
            Assert(auditCount2 == 1 && dbPrevAfterRepeat.CancelledAt == dbPrev.CancelledAt,
                "Repeated expiration checks must be idempotent (no duplicate audit logs or timestamp overwrites).");

            // Verify COMPLETED, manually CANCELLED, and Terminal 2's current-day Held Sale are untouched
            var dbCompleted = await db.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == 9002);
            var dbManualCancel = await db.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == 9003);
            var dbT2 = await db.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == todayT2!.HeldSaleId);
            Assert(dbCompleted.Status == "COMPLETED" && dbCompleted.CancelledAt == null, "Completed Held Sale must remain unchanged.");
            Assert(dbManualCancel.Status == "CANCELLED" && dbManualCancel.CancellationReason == "Customer changed mind" && dbManualCancel.CancelledAt == manualCancelTime,
                "Manually cancelled Held Sale must preserve its original reason & timestamp.");
            Assert(dbT2.Status == "HELD", "Terminal 2 / Outlet 2 current-day Held Sale must remain HELD and unaffected.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    private static async Task TestHeldExp02_ResumeProtectionForDirectOrStaleReference()
    {
        try
        {
            var (conn, db, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Seed a stale HELD record from previous business date (Nov 18 10:00 PM PHT = 14:00 UTC)
            db.HeldSales.Add(new HeldSale
            {
                HeldSaleId = 9501,
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                HoldReference = "HS-STALE-DIRECT",
                CustomerName = "Stale Reference Guest",
                Subtotal = 500m,
                TotalAmount = 500m,
                Status = "HELD",
                BusinessDate = "2026-11-18",
                CreatedAt = new DateTime(2026, 11, 18, 14, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();

            // Current time: Nov 19, 2026 10:00 AM PHT (02:00 UTC) -> Business Date 2026-11-19
            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 19, 2, 0, 0, DateTimeKind.Utc);

            var ctrl = CreateHeldSalesControllerForTest(db, "cashier1", "Cashier", branchId: 1, terminalId: 1);

            // Attempt direct resume WITHOUT calling GetHeldSales first!
            var res1 = await ctrl.ResumeHeldSale(9501, terminalId: 1);
            Assert(res1.Result is ConflictObjectResult conflict1 &&
                   string.Equals(conflict1.Value?.ToString(), HeldSalePolicies.ExpiredHeldSaleResumeMessage, StringComparison.Ordinal),
                $"Direct resume of previous-day Held Sale must return 409 Conflict with exact expired message; got {res1.Result?.GetType().Name}.");

            var persisted = await db.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == 9501);
            Assert(persisted.Status == "CANCELLED" &&
                   persisted.CancelledAt.HasValue &&
                   persisted.CancellationReason == HeldSalePolicies.PreviousBusinessDayCancellationReason,
                "Blocked resume must transition the stale record to CANCELLED with reason & timestamp.");

            // Second resume attempt on already-expired record -> still returns exact message, zero duplicate audit logs
            var res2 = await ctrl.ResumeHeldSale(9501, terminalId: 1);
            Assert(res2.Result is ConflictObjectResult conflict2 &&
                   string.Equals(conflict2.Value?.ToString(), HeldSalePolicies.ExpiredHeldSaleResumeMessage, StringComparison.Ordinal),
                "Repeat resume of already-expired Held Sale must return exact expired message.");
            int audits = await db.HeldSaleAuditLogs.CountAsync(a => a.HeldSaleId == 9501 && a.Action == "CANCELLED");
            Assert(audits == 1, $"Expected exactly 1 audit log after repeated resume attempts, got {audits}.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    private static async Task TestHeldExp03_OfflineExpirationRestartPersistenceAndReconnectSync()
    {
        var prevDbPath = AppContext.GetData("POS.LocalDatabasePath");
        var tempDir = Path.Combine(Path.GetTempPath(), $"pos_held_exp03_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "local_pos.db");

        try
        {
            AppContext.SetData("POS.LocalDatabasePath", dbPath);
            LocalPosDbContext.ResetInitializationState(dbPath);

            var (conn, apiDb, _) = await CreateTestEnvAsync();
            await using var _ = conn;

            // 1. Nov 18, 2026 09:00 PM PHT (2026-11-18 13:00 UTC) -> Business Date 2026-11-18
            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 18, 13, 0, 0, DateTimeKind.Utc);

            using (var seedLocal = new LocalPosDbContext())
            {
                seedLocal.InitializeDatabase();
            }

            var session = new PosSessionService();
            session.SetSession("OFFLINE-SESSION-TOKEN", "cashier1", "Cashier", new TerminalConfigDto
            {
                TerminalId = 1, OutletId = 1, BranchId = 1, TerminalCode = "T-01", TerminalName = "POS 1", BranchName = "Main", BranchCode = "BR-01"
            });

            var connectivity = new ToggleableConnectivityService();
            using var heldHttp = CreateInMemoryHeldSalesHttpClient(apiDb, "cashier1", "Cashier", 1, 1);
            var heldService = new ClientHeldSaleService(heldHttp, connectivity, session);

            // Create 1 Held Sale ONLINE (cached in SQLite) and 1 Held Sale OFFLINE (only in SQLite) on Business Date 2026-11-18
            connectivity.SetOnline(true);
            var onlinePrevHeld = await heldService.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", CustomerName = "Online Prev Day",
                Subtotal = 300m, TotalAmount = 300m,
                Items = new() { new HeldSaleItemDto { ProductId = 1, ProductName = "Pork BBQ", Quantity = 2, BasePrice = 150m, LineTotal = 300m } }
            });
            Assert(onlinePrevHeld != null && onlinePrevHeld.HeldSaleId > 0 && onlinePrevHeld.BusinessDate == "2026-11-18",
                "Online previous-day Held Sale must be created with BusinessDate='2026-11-18'.");

            connectivity.SetOnline(false);
            var offlinePrevHeld = await heldService.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", CustomerName = "Offline Prev Day",
                Notes = "Keep warm", Subtotal = 450m, TotalAmount = 450m,
                Items = new()
                {
                    new HeldSaleItemDto
                    {
                        ProductId = 2, ProductName = "Lechon Belly", Quantity = 0.5m, BasePrice = 900m, LineTotal = 450m,
                        SpecialInstructions = "Chopped",
                        Modifiers = new() { new HeldSaleModifierDto { ModifierOptionId = 5, ModifierName = "Spiced Vinegar", AdditionalPrice = 0m } }
                    }
                }
            });
            Assert(offlinePrevHeld != null && offlinePrevHeld.HeldSaleId < 0 && offlinePrevHeld.BusinessDate == "2026-11-18",
                "Offline previous-day Held Sale must be created locally with BusinessDate='2026-11-18'.");

            // 2. Advance clock past 4:00 AM cutoff while STILL OFFLINE: Nov 19, 2026 04:05 AM PHT (2026-11-18 20:05 UTC)
            BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 11, 18, 20, 5, 0, DateTimeKind.Utc);
            Assert(BusinessDayClock.GetCurrentBusinessDate() == new DateOnly(2026, 11, 19), "Clock must now be on Business Date 2026-11-19.");

            // Create a current-day offline Held Sale on Terminal 1 AND seed a current-day Held Sale on Terminal 2
            var offlineTodayHeld = await heldService.CreateHeldSaleAsync(new HeldSaleCreateDto
            {
                BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", CustomerName = "Offline Today",
                Subtotal = 180m, TotalAmount = 180m,
                Items = new() { new HeldSaleItemDto { ProductId = 1, ProductName = "Tapsilog", Quantity = 1, BasePrice = 180m, LineTotal = 180m } }
            });
            Assert(offlineTodayHeld != null && offlineTodayHeld.BusinessDate == "2026-11-19",
                "Current-day offline Held Sale must have BusinessDate='2026-11-19'.");

            using (var term2Db = new LocalPosDbContext())
            {
                term2Db.HeldSales.Add(new LocalHeldSale
                {
                    HeldSaleId = -8888, BranchId = 1, TerminalId = 2, CashierId = "cashier2",
                    HoldReference = "HOLD-T2-TODAY", Status = "HELD", BusinessDate = "2026-11-19",
                    CreatedAt = BusinessDayClock.UtcNow(),
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new HeldSaleDto
                    {
                        HeldSaleId = -8888, BranchId = 1, OutletId = 2, TerminalId = 2, CashierId = "cashier2",
                        HoldReference = "HOLD-T2-TODAY", Status = "HELD", BusinessDate = "2026-11-19", CreatedAt = BusinessDayClock.UtcNow()
                    })
                });
                await term2Db.SaveChangesAsync();
            }

            // 3. Direct offline resume attempt on stale offlinePrevHeld -> MUST be rejected with exact message & auto-cancelled!
            string? resumeError = null;
            try { await heldService.ResumeHeldSaleAsync(offlinePrevHeld!.HeldSaleId, 1); }
            catch (InvalidOperationException ex) { resumeError = ex.Message; }
            Assert(resumeError == HeldSalePolicies.ExpiredHeldSaleResumeMessage,
                $"Offline resume of expired Held Sale must throw exact message, got '{resumeError}'.");

            // 4. Load Held Sales while offline -> ONLY offlineTodayHeld is visible; onlinePrevHeld is auto-cancelled locally; T2 untouched!
            var offlineList = await heldService.GetHeldSalesAsync(1, 1, 1);
            Assert(offlineList.Count == 1 && offlineList[0].HeldSaleId == offlineTodayHeld!.HeldSaleId,
                $"Offline Held Sales list must contain only today's Held Sale, got {offlineList.Count}.");

            // 5. Simulate POS.Client restart while still offline -> expired records stay CANCELLED with full audit & item data intact!
            LocalPosDbContext.ResetInitializationState(dbPath);
            using (var verifyLocal = new LocalPosDbContext())
            {
                verifyLocal.InitializeDatabase();
                var locOffPrev = await verifyLocal.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == offlinePrevHeld!.HeldSaleId);
                var locOnPrev = await verifyLocal.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == onlinePrevHeld!.HeldSaleId);
                var locT2 = await verifyLocal.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == -8888);

                Assert(locOffPrev.Status == "CANCELLED" && locOffPrev.CancelledAt.HasValue &&
                       locOffPrev.CancellationReason == HeldSalePolicies.PreviousBusinessDayCancellationReason &&
                       locOffPrev.PayloadJson.Contains("Spiced Vinegar") && locOffPrev.PayloadJson.Contains("Chopped"),
                    "Offline expired Held Sale must persist CANCELLED status across restart with items/modifiers/instructions intact.");
                Assert(locOnPrev.Status == "CANCELLED" && locOnPrev.CancelledAt.HasValue &&
                       locOnPrev.CancellationReason == HeldSalePolicies.PreviousBusinessDayCancellationReason,
                    "Cached online Held Sale from previous business date must be marked CANCELLED locally while offline.");
                Assert(locT2.Status == "HELD", "Other terminal's current-day local Held Sale must remain HELD.");
            }

            var restartedService = new ClientHeldSaleService(heldHttp, connectivity, session);
            var afterRestartOfflineList = await restartedService.GetHeldSalesAsync(1, 1, 1);
            Assert(afterRestartOfflineList.Count == 1 && afterRestartOfflineList[0].HeldSaleId == offlineTodayHeld!.HeldSaleId,
                "After offline restart, only current-day Held Sale must be visible.");

            // 6. Reconnect -> Server expires onlinePrevHeld, both sides agree on CANCELLED, nothing resurrected, no duplicates!
            connectivity.SetOnline(true);
            var reconnectedList = await restartedService.GetHeldSalesAsync(1, 1, 1);
            Assert(reconnectedList.Count == 1 && reconnectedList[0].HeldSaleId == offlineTodayHeld!.HeldSaleId,
                $"After reconnect, expired Held Sales must NOT be restored as active; got {reconnectedList.Count}.");

            var srvOnlinePrev = await apiDb.HeldSales.AsNoTracking().SingleAsync(h => h.HeldSaleId == onlinePrevHeld!.HeldSaleId);
            Assert(srvOnlinePrev.Status == "CANCELLED" && srvOnlinePrev.CancelledAt.HasValue &&
                   srvOnlinePrev.CancellationReason == HeldSalePolicies.PreviousBusinessDayCancellationReason,
                "Server DB must agree on CANCELLED status with timestamp & reason after reconnect.");
            int srvAudits = await apiDb.HeldSaleAuditLogs.CountAsync(a => a.HeldSaleId == onlinePrevHeld!.HeldSaleId && a.Action == "CANCELLED");
            Assert(srvAudits == 1, $"Expected exactly 1 server CANCELLED audit log after reconnect, got {srvAudits}.");

            // Current-day Held Sale can still be resumed cleanly!
            var resumedToday = await restartedService.ResumeHeldSaleAsync(offlineTodayHeld!.HeldSaleId, 1);
            Assert(resumedToday != null && resumedToday.Status == "RESUMED" && resumedToday.Items.Count == 1,
                "Current-day Held Sale must remain resumable.");
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
