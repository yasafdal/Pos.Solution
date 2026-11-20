using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Tests;

public static partial class Program
{
    private static CashierSessionsController CreateScopedSessionsController(
        PosDbContext db,
        string username,
        string role,
        int terminalId,
        int branchId = 1)
    {
        var controller = new CashierSessionsController(db);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Name, username),
            new Claim(ClaimTypes.Role, role),
            new Claim("branch_id", branchId.ToString()),
            new Claim("terminal_id", terminalId.ToString())
        ], "TestAuth");
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    // Scenarios 1-9, 13, 14, 15 (Online Server + API + Multi-Terminal + Failed Close + X/Z-Reading):
    // 1. Cashier A logs in & opens shift on T1.
    // 2. Cashier A logs out without closing shift -> ownership preserved.
    // 3. Cashier B blocked across PinLogin, Login, IssueSessionToken, ActivateSession, OpenShift, OrdersController.
    // 4. Cashier A logs back in -> resumes existing CashierSession & CashDrawerShift.
    // 5. Zero duplicate CashierSession or CashDrawerShift rows; financial balances preserved.
    // 15. Failed Close Cashier never releases ownership & never applies closing restriction.
    // 13. Anytime X-Reading still works while shift is OPEN.
    // 6. Cashier A completes Close Cashier with denomination breakdown -> releases ownership.
    // 7. Cashier A blocked on T1 same Business Date (403).
    // 8. Cashier B can now log in & open new shift on T1.
    // 9. Cashier A can still log in & open shift on T2.
    // 14. Successful Z-Reading locks T1 (423) while T2 stays operational.
    private static async Task TestShiftOwnership01_OnlineLogoutResumeCloseReleaseMultiTerminalAndReadings()
    {
        try
        {
            var bizDate = new DateOnly(2026, 11, 20);
            var (dayStartUtc, _) = BusinessDayClock.GetPeriodUtc(bizDate);
            var nowUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc); // 2:00 PM Manila
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, seededShift) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Clear seeded open shift so Scenario 1 starts fresh on Terminal 1; add Terminal 2 & Senior Cashier
            db.CashDrawerShifts.Remove(seededShift);
            var t1 = await db.Terminals.SingleAsync(t => t.TerminalId == 1);
            t1.Uuid = "uuid-own-t1";
            db.Terminals.Add(new Terminal
            {
                TerminalId = 2,
                BranchId = 1,
                OutletId = 1,
                TerminalCode = "POS-02",
                Name = "Terminal 2",
                DeviceIdentifier = "DEV-T2",
                Uuid = "uuid-own-t2",
                IsActive = true
            });
            db.Users.Add(new User { UserId = 4, Username = "senior1", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass"), PinCode = "4444", Role = "Senior Cashier" });
            var u1 = await db.Users.SingleAsync(u => u.Username == "cashier1");
            u1.PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass1");
            var u2 = await db.Users.SingleAsync(u => u.Username == "cashier2");
            u2.PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass2");
            db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = bizDate, ConfiguredBy = "verified" });
            await db.SaveChangesAsync();

            var cfg = BuildWorkflowJwtConfig();
            var auth = new AuthController(db, cfg);
            var terms = new TerminalsController(db, cfg);
            var sessionsA = CreateScopedSessionsController(db, "cashier1", "Cashier", terminalId: 1);
            var sessionsB = CreateScopedSessionsController(db, "cashier2", "Cashier", terminalId: 1);
            var cdT1A = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 1);
            var cdT1B = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);

            // Scenario 1: Cashier A logs in, activates session, and opens shift on Terminal 1
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 1: Cashier A PinLogin on T1 must succeed.");
            var actA1 = await sessionsA.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-own-t1" });
            Assert(actA1 is OkObjectResult, "Scenario 1: Cashier A ActivateSession on T1 must succeed.");
            var openARes = await cdT1A.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                ClientShiftId = "own01-shift-a",
                OpeningFloat = 1000m,
                OpenedAt = dayStartUtc.AddHours(2)
            }) as OkObjectResult;
            Assert(openARes != null, "Scenario 1: Cashier A OpenShift on T1 must succeed.");
            var shiftA = (CashDrawerShiftDto)openARes!.Value!;

            // Simulate sales & cash movement on Cashier A's open shift
            var shiftAEntity = await db.CashDrawerShifts.SingleAsync(s => s.ShiftId == shiftA.ShiftId);
            shiftAEntity.TotalCashSales = 650m;
            shiftAEntity.ExpectedBalance = 1650m;
            await db.SaveChangesAsync();

            // Scenario 2: Cashier A logs out (client clears in-memory session ONLY — does NOT close shift or create restriction)
            var clientSession = new PosSessionService();
            clientSession.SetSession("jwt-a", "cashier1", "Cashier", new TerminalConfigDto { TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "T1" });
            clientSession.ClearSession(); // Mirrors MainLayout.PerformSignOutCoreAsync
            Assert(!clientSession.IsAuthenticated && clientSession.Token == null && clientSession.Username == null,
                "Scenario 2: Logout must clear client authentication state.");
            Assert(await db.CashDrawerShifts.CountAsync(s => s.TerminalId == 1 && s.Status == "OPEN" && s.CashierId == "cashier1") == 1,
                "Scenario 2: Logout must preserve Cashier A's open CashDrawerShift.");
            Assert(!await db.CashierClosingRestrictions.AnyAsync(),
                "Scenario 2: Logout must NOT create a Close Cashier restriction.");

            // Scenario 3: Cashier B attempts to log in on T1 via ALL entry points -> MUST BE BLOCKED with exact message!
            var expectedMsg = TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage;

            var bPinLogin = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bPinLogin?.StatusCode == 409 && string.Equals(bPinLogin.Value?.ToString(), expectedMsg, StringComparison.Ordinal),
                $"Scenario 3: PinLogin for Cashier B must return 409 with exact message. Got {bPinLogin?.StatusCode}: '{bPinLogin?.Value}'");

            var bPwdLogin = await auth.Login(new LoginDto { Username = "cashier2", Password = "pass2", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bPwdLogin?.StatusCode == 409 && string.Equals(bPwdLogin.Value?.ToString(), expectedMsg, StringComparison.Ordinal),
                $"Scenario 3: Password Login for Cashier B must return 409 with exact message. Got {bPwdLogin?.StatusCode}");

            var bTokenRes = (await terms.IssueSessionToken(new TerminalSessionTokenRequestDto { TerminalId = 1, DeviceUuid = "uuid-own-t1", CashierUsername = "cashier2" })).Result as ObjectResult;
            Assert(bTokenRes?.StatusCode == 409 && (bTokenRes.Value as TerminalSessionTokenResponseDto)?.Message == expectedMsg,
                $"Scenario 3: IssueSessionToken for Cashier B must return 409 with exact message. Got {bTokenRes?.StatusCode}");

            var bActRes = await sessionsB.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-own-t1" }) as ObjectResult;
            Assert(bActRes?.StatusCode == 409 && string.Equals(bActRes.Value?.ToString(), expectedMsg, StringComparison.Ordinal),
                $"Scenario 3: ActivateSession for Cashier B must return 409 with exact message. Got {bActRes?.StatusCode}");

            var bOpenRes = await cdT1B.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier2", OpeningFloat = 1000m }) as ConflictObjectResult;
            Assert(bOpenRes != null && string.Equals(bOpenRes.Value?.ToString(), expectedMsg, StringComparison.Ordinal),
                $"Scenario 3: OpenShift for Cashier B must return 409 with exact message. Got '{bOpenRes?.Value}'");

            // Scenario 4 & 5: Cashier A logs back into T1 -> resumes existing session & shift with ZERO duplicates!
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 4: Cashier A must be allowed to log back into T1.");
            var actA2 = await sessionsA.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-own-t1" });
            Assert(actA2 is OkObjectResult, "Scenario 4: Cashier A ActivateSession resume must succeed.");

            int sessionCountA = await db.CashierSessions.CountAsync(s => s.TerminalId == 1 && s.CashierId == "cashier1");
            int activeShiftCount = await db.CashDrawerShifts.CountAsync(s => s.TerminalId == 1 && s.Status == "OPEN");
            Assert(sessionCountA == 1 && activeShiftCount == 1,
                $"Scenario 5: Same cashier re-login must NOT duplicate CashierSession (got {sessionCountA}) or CashDrawerShift (got {activeShiftCount}).");
            Assert(await db.CashierSwitchLogs.CountAsync() == 0,
                "Scenario 5: Same cashier re-login must NOT create a bogus CashierSwitchLog.");

            var resumedShiftRes = (await cdT1A.GetCurrentShift(1)).Result as OkObjectResult;
            var resumedShift = resumedShiftRes?.Value as CashDrawerShiftDto;
            Assert(resumedShift != null && resumedShift.ShiftId == shiftA.ShiftId && resumedShift.OpeningFloat == 1000m,
                "Scenario 4 & 5: Resumed shift must preserve original ShiftId and OpeningFloat.");

            // Scenario 13: Anytime X-Reading remains available while Cashier A's shift is OPEN
            var cdMgr = CreateTerminalScopedCashDrawerController(db, "manager1", "Manager", terminalId: 1);
            var xReadRes = (await cdMgr.GetAnytimeXReading(1, "2026-11-20")).Result as OkObjectResult;
            var xRead = xReadRes?.Value as XReadingReportDto;
            Assert(xRead != null && xRead.IsAnytimeAudit && xRead.ActiveShiftCount == 1,
                "Scenario 13: Anytime X-Reading must work read-only while Cashier A's shift is OPEN.");

            // Scenario 15: Failed Close Cashier (pending approval movement) NEVER releases ownership or creates restriction!
            db.CashDrawerMovements.Add(new CashDrawerMovement
            {
                ShiftId = shiftA.ShiftId,
                BranchId = 1,
                TerminalId = 1,
                MovementType = "PETTY_CASH",
                Amount = 50m,
                Reason = "Pending approval blocks close",
                Status = "PENDING_APPROVAL",
                CreatedBy = "cashier1",
                AuthorizedBy = "cashier1",
                CreatedAt = nowUtc.AddMinutes(-5)
            });
            await db.SaveChangesAsync();

            var failClose = await cdT1A.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftA.ShiftId,
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto> { new() { Type = "BILL", Denomination = 1000m, Quantity = 1 } }
            });
            Assert(failClose is BadRequestObjectResult, "Scenario 15: CloseShift with pending movement must fail.");
            Assert(await db.CashDrawerShifts.AnyAsync(s => s.ShiftId == shiftA.ShiftId && s.Status == "OPEN"),
                "Scenario 15: Failed Close Cashier must keep shift OPEN.");
            Assert(!await db.CashierClosingRestrictions.AnyAsync(),
                "Scenario 15: Failed Close Cashier must NOT apply closing restriction.");
            var bStillBlocked = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bStillBlocked?.StatusCode == 409, "Scenario 15: Cashier B must remain blocked after failed Close Cashier.");

            // Reject pending movement so Scenario 6 Close Cashier succeeds
            (await db.CashDrawerMovements.SingleAsync()).Status = "REJECTED";
            await db.SaveChangesAsync();

            // Scenario 6: Cashier A completes Close Cashier with denomination counting -> releases ownership & restricts (A, T1, Date)
            var closeARes = await cdT1A.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftA.ShiftId,
                ClientCloseId = "own01-close-a",
                ClosedAt = nowUtc.AddMinutes(-10),
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 500m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 100m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 50m, Quantity = 1 }
                }
            });
            Assert(closeARes is OkObjectResult, "Scenario 6: Valid Close Cashier with denomination breakdown must succeed.");

            // Scenario 7: Cashier A cannot log in again on Terminal 1 for the same Business Date (403)
            var aBlockedT1 = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(aBlockedT1?.StatusCode == 403, $"Scenario 7: Cashier A must get 403 on T1 after Close Cashier, got {aBlockedT1?.StatusCode}");

            // Scenario 8: Cashier B can NOW log in and open a new shift on Terminal 1!
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 8: Cashier B must be allowed to log in on T1 after Cashier A closes.");
            Assert((await sessionsB.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-own-t1" })) is OkObjectResult,
                "Scenario 8: Cashier B ActivateSession on T1 must succeed after ownership release.");
            var openBRes = await cdT1B.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                ClientShiftId = "own01-shift-b",
                OpeningFloat = 1000m,
                OpenedAt = nowUtc.AddMinutes(-5)
            }) as OkObjectResult;
            Assert(openBRes != null, "Scenario 8: Cashier B must be able to open a new shift on T1.");
            var shiftB = (CashDrawerShiftDto)openBRes!.Value!;

            // Scenario 9: Cashier A can still log in and open a shift on Terminal 2!
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 2, BranchId = 1 })) is OkObjectResult,
                "Scenario 9: Cashier A must still be allowed to log in on Terminal 2.");
            var cdT2A = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 2);
            Assert((await cdT2A.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 2,
                CashierId = "cashier1",
                ClientShiftId = "own01-shift-a-t2",
                OpeningFloat = 1000m,
                OpenedAt = nowUtc
            })) is OkObjectResult, "Scenario 9: Cashier A must be able to open a shift on Terminal 2.");

            // Close Cashier B on T1 so Scenario 14 Z-Reading can finalize T1
            Assert((await cdT1B.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftB.ShiftId,
                ClientCloseId = "own01-close-b",
                ClosedAt = nowUtc.AddMinutes(-1),
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto> { new() { Type = "BILL", Denomination = 1000m, Quantity = 1 } }
            })) is OkObjectResult, "Cashier B close on T1 must succeed.");

            // Scenario 14: Z-Reading finalizes and locks Terminal 1 (423), while Terminal 2 stays operational!
            var zSenior = CreateTerminalScopedZController(db, cfg, "senior1", "Senior Cashier", terminalId: 1);
            var zRes = await zSenior.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "own01-z-t1",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            });
            Assert(zRes is OkObjectResult, "Scenario 14: Z-Reading on T1 must succeed once all shifts are closed.");

            var t1LockedLogin = await auth.PinLogin(new PinLoginDto { PinCode = "4444", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(t1LockedLogin?.StatusCode == 423, $"Scenario 14: T1 must return 423 Locked after Z-Reading, got {t1LockedLogin?.StatusCode}");

            var t2StillOpenLogin = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 2, BranchId = 1 });
            Assert(t2StillOpenLogin is OkObjectResult, "Scenario 14: Terminal 2 must remain operational while Terminal 1 is Z-locked.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenarios 10, 11 & 12 (Offline Logout, Blocked Login, Restart Persistence & Idempotent Reconnect Sync):
    // 10. Offline: Cashier A opens shift, sells offline, logs out -> Cashier B blocked offline on T1; Cashier A resumes offline.
    // 11. Restart POS.Client while Cashier A has open shift -> ownership persists in SQLite; Cashier B still blocked; Cashier A resumes intact.
    // 12. Reconnect: network failure never treats shift as closed; reconnect sync uploads SHIFT_OPEN + Order + SHIFT_CLOSE cleanly with ZERO duplicates,
    //     and Cashier B can log in only after Cashier A completes Close Cashier.
    private static async Task TestShiftOwnership02_OfflineLogoutBlockedLoginRestartPersistenceAndReconnectSync()
    {
        try
        {
            var dayUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);
            SetDeterministicManilaClock(dayUtc, tickCount64: 200_000L);

            using var localScope = new TempLocalDbScope("own02_offline");
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);

            using (var seedDb = new LocalPosDbContext())
            {
                seedDb.InitializeDatabase();
                string HashPin(string p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p)));
                seedDb.Cashiers.RemoveRange(seedDb.Cashiers);
                seedDb.Cashiers.AddRange(
                    new LocalCashier { UserId = 1, Username = "cashier1", Role = "Cashier", PinHash = HashPin("1111"), SyncedAtUtc = dayUtc },
                    new LocalCashier { UserId = 2, Username = "cashier2", Role = "Cashier", PinHash = HashPin("2222"), SyncedAtUtc = dayUtc },
                    new LocalCashier { UserId = 3, Username = "manager1", Role = "Manager", PinHash = HashPin("9999"), SyncedAtUtc = dayUtc });
                await seedDb.SaveChangesAsync();
            }

            using var noNetHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline")))
            {
                BaseAddress = new Uri("http://localhost/")
            };

            // 1. Offline: Cashier A opens shift and completes an offline sale (₱224)
            var shiftA = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = "own02-shift-a",
                OpeningFloat = 1000m,
                OpenedAt = dayUtc
            }, "cashier1", offline: true);

            var sessionA = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using (var checkoutDb = new LocalPosDbContext())
            {
                var sale = await new OfflineCheckoutService(checkoutDb, sessionA).ProcessSaleAsync(
                    BuildSingleCashOrderDto(shiftA.ShiftId, shiftA.ClientShiftId!, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(sale.Success, $"Offline sale for Cashier A failed: {sale.ErrorMessage}");
            }

            // 2. Offline Logout (clears in-memory auth only; does NOT close shift)
            sessionA.ClearSession();
            Assert(!sessionA.IsAuthenticated, "Offline logout must clear in-memory authentication.");

            // Scenario 10: Cashier B attempts offline login & shift open on T1 -> MUST BE BLOCKED!
            var lockBOffline = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(lockBOffline.IsShiftOwnedByAnotherCashier &&
                   lockBOffline.ActiveShiftOwnerCashierId == "cashier1" &&
                   lockBOffline.ActiveShiftOwnershipMessage == TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage,
                $"Scenario 10: Offline lock status must block Cashier B with exact message. Got OwnedByOther={lockBOffline.IsShiftOwnedByAnotherCashier}, Owner={lockBOffline.ActiveShiftOwnerCashierId}");

            // Network failure on online probe must ALSO fall back to local ownership and NEVER treat shift as closed!
            var lockBOnNetworkFail = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: false);
            Assert(lockBOnNetworkFail.IsShiftOwnedByAnotherCashier,
                "Scenario 10/12: Network failure must NEVER treat an open shift as closed.");

            bool bBlockedOfflineOpen = false;
            try
            {
                await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
                {
                    BranchId = 1,
                    TerminalId = 1,
                    ClientShiftId = "own02-shift-b-blocked",
                    OpeningFloat = 1000m
                }, "cashier2", offline: true);
            }
            catch (InvalidOperationException ex) when (ex.Message == TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage)
            {
                bBlockedOfflineOpen = true;
            }
            Assert(bBlockedOfflineOpen, "Scenario 10: Offline OpenAsync for Cashier B must throw exact ownership message.");

            // Scenario 11: Simulate POS.Client restart while Cashier A's shift is still open -> ownership persists intact!
            LocalPosDbContext.ResetInitializationState();
            var lockBAfterRestart = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(lockBAfterRestart.IsShiftOwnedByAnotherCashier && lockBAfterRestart.ActiveShiftOwnerCashierId == "cashier1",
                "Scenario 11: After app restart, Cashier A's open shift ownership must remain intact in SQLite.");

            // Cashier A logs back in offline after restart -> allowed and resumes exact existing shift with ₱1,224 expected balance!
            var lockAAfterRestart = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier1", offline: true);
            Assert(!lockAAfterRestart.IsShiftOwnedByAnotherCashier && !lockAAfterRestart.IsCashierRestricted && !lockAAfterRestart.IsTerminalLocked,
                "Scenario 11: Cashier A must be allowed to log back in offline after restart.");

            var resumedSessionA = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            var verifyA = await ShiftSession.VerifyPaymentShiftLocalFirstAsync(noNetHttp, resumedSessionA, new PosConnectivityService(noNetHttp));
            Assert(verifyA.IsReady && verifyA.Shift != null &&
                   verifyA.Shift.ClientShiftId == "own02-shift-a" &&
                   verifyA.Shift.OpeningFloat == 1000m &&
                   verifyA.Shift.TotalCashSales == 224m &&
                   verifyA.Shift.ExpectedBalance == 1224m,
                $"Scenario 11: Resumed shift must preserve OpeningFloat=1000, CashSales=224, Expected=1224. Got Ready={verifyA.IsReady}, Exp={verifyA.Shift?.ExpectedBalance}");

            // Offline X-Reading still works anytime while Cashier A's shift is OPEN!
            var offlineX = await ShiftSession.GetAnytimeXReadingAsync(noNetHttp, 1, new DateOnly(2026, 11, 20), offline: true, supervisorPin: "9999");
            Assert(offlineX.IsAnytimeAudit && offlineX.ActiveShiftCount == 1 && offlineX.NetSales == 224m,
                "Scenario 13 (Offline): Offline X-Reading must work while Cashier A's shift is OPEN.");

            // Cashier A completes Close Cashier offline -> releases local ownership & restricts Cashier A
            var closedA = await ShiftSession.CloseAsync(noNetHttp, new CloseShiftDto
            {
                ShiftId = shiftA.ShiftId,
                ClientShiftId = shiftA.ClientShiftId,
                ClientCloseId = "own02-close-a",
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 200m, Quantity = 1 },
                    new() { Type = "COIN", Denomination = 20m, Quantity = 1 },
                    new() { Type = "COIN", Denomination = 1m, Quantity = 4 }
                },
                ClosedAt = dayUtc.AddMinutes(30)
            }, "cashier1", offline: true);
            Assert(closedA.Status == "CLOSED", "Offline Close Cashier A must succeed.");

            var lockAAfterClose = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier1", offline: true);
            var lockBAfterClose = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(lockAAfterClose.IsCashierRestricted && !lockAAfterClose.IsTerminalLocked,
                "Offline Close Cashier must restrict Cashier A without locking terminal.");
            Assert(!lockBAfterClose.IsShiftOwnedByAnotherCashier && !lockBAfterClose.IsCashierRestricted && !lockBAfterClose.IsTerminalLocked,
                "Offline Close Cashier must release ownership so Cashier B can now log in offline.");

            // Scenario 12: Reconnect & synchronize to in-proc server without duplicates!
            var (srvConn, srvDb, srvSeedShift) = await CreateTestEnvAsync();
            await using var _srv = srvConn;
            var srvTerm = await srvDb.Terminals.SingleAsync(t => t.TerminalId == 1);
            srvTerm.DeviceIdentifier = "DEV-UUID-1";
            srvTerm.Uuid = "DEV-UUID-1";
            srvSeedShift.Status = "CLOSED";
            srvSeedShift.OpenedAt = dayUtc.AddDays(-2);
            srvSeedShift.ClosedAt = dayUtc.AddDays(-2).AddHours(4);
            srvSeedShift.ActualCountedBalance = srvSeedShift.OpeningFloat;
            srvSeedShift.ClosingOriginalDeclarationJson = "{}";
            srvDb.Categories.Add(new Category { CategoryId = 1, Name = "Main", IsActive = true });
            srvDb.Products.Add(new Product { ProductId = 1, Name = "Lechon Meal", Price = 224m, CategoryId = 1, ProductType = "FinishedGood", IsActive = true });
            srvDb.InvoiceSequences.Add(new InvoiceSequence
            {
                TerminalId = 1,
                SeriesCode = "TXN1",
                Prefix = "TXN1-",
                StartingNumber = 1,
                EndingNumber = 10000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            await srvDb.SaveChangesAsync();

            var srvCashDrawer = CreateController(srvDb, "POS-1", "Terminal");
            var srvOfflineSync = CreateTerminalSyncController(srvDb);

            using var syncHttp = new HttpClient(new InProcSyncHandler(async req =>
            {
                string path = req.RequestUri!.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith("/api/cash-drawer/open-shift"))
                    return ObjectResultToHttpResponse(await srvCashDrawer.OpenShift((await req.Content!.ReadFromJsonAsync<OpenShiftDto>())!));
                if (path.EndsWith("/api/cash-drawer/close-shift"))
                    return ObjectResultToHttpResponse(await srvCashDrawer.CloseShift((await req.Content!.ReadFromJsonAsync<CloseShiftDto>())!));
                if (path.EndsWith("/api/offline-sync/transactions"))
                    return ActionResultToHttpResponse(await srvOfflineSync.SyncTransaction((await req.Content!.ReadFromJsonAsync<OfflineTransactionSyncRequest>())!));
                if (path.Contains("/api/offline-sync/transactions/") && path.EndsWith("/status"))
                {
                    var txId = req.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2];
                    return ObjectResultToHttpResponse(await srvOfflineSync.GetTransactionStatus(Uri.UnescapeDataString(txId)));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }))
            { BaseAddress = new Uri("http://localhost/") };

            using var syncDb = new LocalPosDbContext();
            var syncSvc = new OfflineTransactionSyncService(syncDb);
            var pending = await syncSvc.GetPendingOutboxItemsAsync();
            Assert(pending.Count == 3, $"Expected 3 pending outbox items (SHIFT_OPEN, Order, SHIFT_CLOSE), got {pending.Count}.");

            foreach (var item in pending)
            {
                bool uploaded = await syncSvc.UploadTransactionAsync(item, syncHttp);
                if (!uploaded)
                {
                    using var errDb = new LocalPosDbContext();
                    var failedRow = await errDb.SyncOutboxes.AsNoTracking().SingleOrDefaultAsync(o => o.OutboxId == item.OutboxId);
                    Assert(false, $"Sync upload failed for {item.EntityType}/{item.EntityId}: {failedRow?.LastError}");
                }
            }
            // Replay every outbox item a second time to verify idempotency (zero duplicate shifts/orders/restrictions!)
            foreach (var item in pending)
            {
                Assert(await syncSvc.UploadTransactionAsync(item, syncHttp), $"Idempotent replay failed for {item.EntityType}/{item.EntityId}.");
            }

            Assert(await srvDb.CashDrawerShifts.CountAsync(s => s.ClientShiftId == "own02-shift-a") == 1,
                "Scenario 12: Reconnect sync must produce exactly 1 CashDrawerShift on server.");
            Assert(await srvDb.Orders.CountAsync(o => o.TerminalId == 1) == 1,
                "Scenario 12: Reconnect sync must produce exactly 1 Order on server.");
            Assert(await srvDb.CashierClosingRestrictions.CountAsync(r => r.TerminalId == 1 && r.CashierId == "cashier1") == 1,
                "Scenario 12: Reconnect sync must produce exactly 1 CashierClosingRestriction on server.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }
}
