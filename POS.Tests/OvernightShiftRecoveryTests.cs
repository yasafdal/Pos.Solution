using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    private static OrdersController CreateScopedOrdersController(
        PosDbContext db,
        string username,
        string role,
        int terminalId,
        int branchId = 1)
    {
        var controller = new OrdersController(db, new SequenceService(db), new DiscountCalculationService());
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

    // Scenarios 1-12, 16, 17 (Online E2E: Unclosed Nov 20 Shift -> 4:00 AM Nov 21 -> Detection, Blocked Takeover,
    // Multi-Terminal Isolation, Supervisor Overnight Closing with Shortage/Overage, Original Date & Transactions Preserved,
    // Cashier B Fresh Nov 21 Shift with Separate Opening Fund, and Z-Reading Readiness & Genuine Z-Lock Preservation)
    private static async Task TestOvernightRecovery01_OnlineDetectionBlockingSupervisorCloseReleaseMultiTerminalAndZReading()
    {
        try
        {
            var nov20 = new DateOnly(2026, 11, 20);
            var nov21 = new DateOnly(2026, 11, 21);
            var nov20AfternoonUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc); // Nov 20 2:00 PM Manila
            var nov21MorningUtc = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);   // Nov 21 8:00 AM Manila (past 4:00 AM!)
            BusinessDayClock.UtcNowProvider = () => nov20AfternoonUtc;

            var (conn, db, seededShift) = await CreateTestEnvAsync();
            await using var _ = conn;

            db.CashDrawerShifts.Remove(seededShift);
            var t1 = await db.Terminals.SingleAsync(t => t.TerminalId == 1);
            t1.Uuid = "uuid-ovn-t1";
            db.Terminals.Add(new Terminal
            {
                TerminalId = 2,
                BranchId = 1,
                OutletId = 1,
                TerminalCode = "POS-02",
                Name = "Terminal 2",
                DeviceIdentifier = "DEV-OVN-T2",
                Uuid = "uuid-ovn-t2",
                IsActive = true
            });
            var u1 = await db.Users.SingleAsync(u => u.Username == "cashier1");
            u1.PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass1");
            var u2 = await db.Users.SingleAsync(u => u.Username == "cashier2");
            u2.PasswordHash = BCrypt.Net.BCrypt.HashPassword("pass2");
            var mgr = await db.Users.SingleAsync(u => u.Username == "manager1");
            mgr.PasswordHash = BCrypt.Net.BCrypt.HashPassword("passmgr");
            db.ZReadingTerminalStates.Add(new ZReadingTerminalState
            {
                TerminalId = 1,
                FirstBusinessDate = nov20,
                ConfiguredBy = "admin"
            });
            await db.SaveChangesAsync();

            var cfg = BuildWorkflowJwtConfig();
            var auth = new AuthController(db, cfg);
            var terms = new TerminalsController(db, cfg);
            var sessionsA = CreateScopedSessionsController(db, "cashier1", "Cashier", terminalId: 1);
            var sessionsB = CreateScopedSessionsController(db, "cashier2", "Cashier", terminalId: 1);
            var sessionsMgr = CreateScopedSessionsController(db, "manager1", "Manager", terminalId: 1);
            var cdT1A = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 1);
            var cdT1B = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);
            var cdT1Mgr = CreateTerminalScopedCashDrawerController(db, "manager1", "Manager", terminalId: 1);
            var cdT2B = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 2);
            var ordersT1B = CreateScopedOrdersController(db, "cashier2", "Cashier", terminalId: 1);
            var ordersT1A = CreateScopedOrdersController(db, "cashier1", "Cashier", terminalId: 1);
            var ordersT1Mgr = CreateScopedOrdersController(db, "manager1", "Manager", terminalId: 1);

            // Scenario 1: Cashier A opens shift on Nov 20 (₱1,000 float), sells ₱350 cash, posts ₱150 paid-in -> Expected ₱1,500
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 1: Cashier A login on Nov 20 must succeed.");
            Assert((await sessionsA.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-ovn-t1" })) is OkObjectResult,
                "Scenario 1: Cashier A ActivateSession on Nov 20 must succeed.");
            var openARes = await cdT1A.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                ClientShiftId = "ovn01-shift-nov20",
                OpeningFloat = 1000m,
                OpenedAt = nov20AfternoonUtc
            }) as OkObjectResult;
            Assert(openARes != null, "Scenario 1: Cashier A OpenShift on Nov 20 must succeed.");
            var shiftNov20 = (CashDrawerShiftDto)openARes!.Value!;
            Assert(shiftNov20.BusinessDate == "2026-11-20", $"Scenario 1: Expected BusinessDate 2026-11-20, got {shiftNov20.BusinessDate}");

            var shiftEntity = await db.CashDrawerShifts.SingleAsync(s => s.ShiftId == shiftNov20.ShiftId);
            shiftEntity.TotalCashSales = 350m;
            shiftEntity.TotalCashIn = 150m;
            shiftEntity.ExpectedBalance = 1500m;
            var nov20Order = new Order
            {
                OrderNumber = "POS01-000001",
                InvoiceNumber = "INV-0000001",
                InvoiceIssuedAt = nov20AfternoonUtc.AddMinutes(15),
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = shiftNov20.ShiftId,
                OrderType = "DINING",
                CashierId = "cashier1",
                Subtotal = 350m,
                DiscountAmount = 0m,
                VatableSales = 312.50m,
                VatAmount = 37.50m,
                TotalAmount = 350m,
                PaymentStatus = "PAID",
                OrderStatus = "COMPLETED",
                CreatedAt = nov20AfternoonUtc.AddMinutes(15)
            };
            db.Orders.Add(nov20Order);
            await db.SaveChangesAsync();
            db.Payments.Add(new Payment
            {
                OrderId = nov20Order.OrderId,
                Amount = 350m,
                PaymentMethod = "CASH",
                PaymentStatus = "PAID",
                ProcessedBy = "cashier1",
                PaidAt = nov20AfternoonUtc.AddMinutes(15)
            });
            db.CashDrawerMovements.Add(new CashDrawerMovement
            {
                ShiftId = shiftNov20.ShiftId,
                BranchId = 1,
                TerminalId = 1,
                MovementType = "CASH_IN",
                Amount = 150m,
                Reason = "Extra change fund",
                Status = "POSTED",
                CreatedBy = "cashier1",
                AuthorizedBy = "cashier1",
                CreatedAt = nov20AfternoonUtc.AddMinutes(20)
            });
            await db.SaveChangesAsync();

            // Scenario 2: Cashier A logs out without Close Cashier
            var clientSession = new PosSessionService();
            clientSession.SetSession("jwt-a", "cashier1", "Cashier", new TerminalConfigDto { TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "T1" });
            clientSession.ClearSession();

            // Scenario 3: Business Date advances past 4:00 AM to Nov 21 8:00 AM Manila
            BusinessDayClock.UtcNowProvider = () => nov21MorningUtc;

            // Verify system NEVER auto-closes the shift and NEVER auto-generates Z-Reading at 4:00 AM
            var persistedNov20Shift = await db.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == shiftNov20.ShiftId);
            Assert(persistedNov20Shift.Status == "OPEN" && persistedNov20Shift.ClosedAt == null,
                "Scenario 3: Unresolved Nov 20 shift must NEVER be auto-closed at 4:00 AM.");
            Assert(!await db.ZReadings.AnyAsync(),
                "Scenario 3 & 17: Z-Reading must NEVER be auto-generated when Business Date changes.");

            // Scenarios 4, 5, 6: Detection & Cashier B blocked from silent takeover across ALL endpoints
            var lockForB = ((await cdT1B.GetTerminalLockStatus(1, "cashier2")).Result as OkObjectResult)?.Value as TerminalClosingLockDto;
            Assert(lockForB != null &&
                   lockForB.HasUnresolvedPreviousDayShift &&
                   lockForB.UnresolvedShiftBusinessDate == "2026-11-20" &&
                   lockForB.UnresolvedShiftCashierId == "cashier1" &&
                   lockForB.UnresolvedShiftId == shiftNov20.ShiftId &&
                   lockForB.UnresolvedShiftRecoveryStatus == TerminalClosingLockDto.RecoveryStatusRequiresReview &&
                   lockForB.IsShiftOwnedByAnotherCashier &&
                   !lockForB.IsTerminalLocked &&
                   lockForB.UnresolvedShiftMessage!.Contains("2026-11-20"),
                "Scenarios 4-5: Terminal lock status must detect unresolved previous-day Nov 20 shift without terminal Z-lock.");

            var bPinLogin = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bPinLogin?.StatusCode == 409 && bPinLogin.Value!.ToString()!.Contains("2026-11-20"),
                $"Scenario 6: Cashier B PinLogin must be blocked (409) with previous Business Date message. Got {bPinLogin?.StatusCode}");
            var bPwdLogin = await auth.Login(new LoginDto { Username = "cashier2", Password = "pass2", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bPwdLogin?.StatusCode == 409, "Scenario 6: Cashier B password Login must be blocked (409).");
            var bSessionToken = (await terms.IssueSessionToken(new TerminalSessionTokenRequestDto { TerminalId = 1, DeviceUuid = "uuid-ovn-t1", CashierUsername = "cashier2" })).Result as ObjectResult;
            Assert(bSessionToken?.StatusCode == 409, "Scenario 6: Cashier B IssueSessionToken must be blocked (409).");
            var bActivate = await sessionsB.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-ovn-t1" }) as ObjectResult;
            Assert(bActivate?.StatusCode == 409, "Scenario 6: Cashier B ActivateSession must be blocked (409).");
            var bOpenShift = await cdT1B.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier2", OpeningFloat = 2000m }) as ConflictObjectResult;
            Assert(bOpenShift != null, "Scenario 6: Cashier B OpenShift must be blocked (409).");

            var dummyOrder = new OrderCreateDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                Items = [new OrderItemCreateDto { ProductId = 1, ProductName = "Test", Quantity = 1, BasePrice = 100m }]
            };
            Assert((await ordersT1B.CreateOrder(dummyOrder)).Result is ConflictObjectResult,
                "Scenario 6: Cashier B CreateOrder must be blocked while Nov 20 shift is unresolved.");

            // Cashier A also cannot silently continue selling or normal-closing Nov 20's unresolved shift on Nov 21
            var aPinLoginNov21 = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(aPinLoginNov21?.StatusCode == 409, "Scenario 6: Regular Cashier A login must require supervisor recovery on Nov 21.");
            dummyOrder.CashierId = "cashier1";
            Assert((await ordersT1A.CreateOrder(dummyOrder)).Result is ConflictObjectResult,
                "Scenario 6: Cashier A cannot ring up Nov 21 sales under Nov 20 unresolved shift.");
            var normalCloseAttempt = await cdT1A.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftNov20.ShiftId,
                IsDeclarationConfirmed = true,
                Denominations = [new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 }]
            });
            Assert(normalCloseAttempt is ConflictObjectResult,
                "Scenario 6: Normal CloseShift must reject closing a previous-Business-Date shift without supervisor overnight recovery.");

            // Scenario 16: Terminal 2 remains 100% operational (NO branch-wide lock!)
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 2, BranchId = 1 })) is OkObjectResult,
                "Scenario 16: Cashier B login on Terminal 2 must succeed while Terminal 1 has an unresolved shift.");
            var t2Open = await cdT2B.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 2,
                CashierId = "cashier2",
                ClientShiftId = "ovn01-t2-shift",
                OpeningFloat = 1500m,
                OpenedAt = nov21MorningUtc
            }) as OkObjectResult;
            Assert(t2Open != null, "Scenario 16: Opening shift on Terminal 2 must succeed (no branch-wide lock).");

            // Scenario 17 (pre-recovery): Z-Reading must detect unresolved Nov 20 shift and block finalization
            var zSvc = new ZReadingService(db, cfg);
            var preReadiness = await zSvc.ReadinessAsync(1, nov20);
            Assert(!preReadiness.IsReady && preReadiness.Blockers.Any(b => b.Contains("2026-11-20") || b.Contains("Open")),
                $"Scenario 17: Z-Reading readiness for Nov 20 must be blocked while shift is OPEN. Blockers: {string.Join("; ", preReadiness.Blockers)}");

            // Scenario 7: Supervisor authenticates on Terminal 1 (allowed for recovery, still blocked from ringing up sales!)
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "9999", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 7: Authorized Manager PinLogin must be permitted on Terminal 1 for overnight recovery.");
            Assert((await sessionsMgr.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-ovn-t1" })) is OkObjectResult,
                "Scenario 7: Manager ActivateSession must succeed for recovery.");
            dummyOrder.CashierId = "manager1";
            Assert((await ordersT1Mgr.CreateOrder(dummyOrder)).Result is ConflictObjectResult,
                "Scenario 7: Even Supervisor cannot process new sales until unresolved shift is closed.");
            Assert((await cdT1Mgr.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "manager1", OpeningFloat = 1000m })) is ConflictObjectResult,
                "Scenario 7: Even Supervisor cannot open a new shift until unresolved shift is closed.");

            // Unauthorized cashier cannot inspect or execute recovery
            Assert((await cdT1B.GetUnresolvedPreviousDayShift(1)).Result is ForbidResult,
                "Security: Cashier B must be forbidden from inspecting unresolved shift.");
            Assert((await cdT1B.SupervisorOvernightClose(new SupervisorOvernightCloseRequestDto
            {
                TerminalId = 1,
                ShiftId = shiftNov20.ShiftId,
                Reason = "Unauthorized attempt",
                IsDeclarationConfirmed = true,
                Denominations = [new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 }]
            })).Result is ForbidResult,
                "Security: Cashier B without supervisor PIN must be forbidden from executing overnight recovery.");

            // Supervisor inspects unresolved shift
            var inspectDto = ((await cdT1Mgr.GetUnresolvedPreviousDayShift(1)).Result as OkObjectResult)?.Value as CashDrawerShiftDto;
            Assert(inspectDto != null &&
                   inspectDto.ShiftId == shiftNov20.ShiftId &&
                   inspectDto.CashierId == "cashier1" &&
                   inspectDto.BusinessDate == "2026-11-20" &&
                   inspectDto.ExpectedBalance == 1500m &&
                   inspectDto.TotalCashSales == 350m &&
                   inspectDto.TotalCashIn == 150m &&
                   inspectDto.OutletId == 1,
                "Scenario 7: Supervisor inspect must return unredacted original cashier, Business Date, expected cash, sales, and movements.");

            // Validation guards: missing reason / unconfirmed / empty denominations must fail with 400
            Assert((await cdT1Mgr.SupervisorOvernightClose(new SupervisorOvernightCloseRequestDto
            {
                TerminalId = 1,
                ShiftId = shiftNov20.ShiftId,
                Reason = "",
                IsDeclarationConfirmed = true,
                Denominations = [new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 }]
            })).Result is BadRequestObjectResult, "Scenario 7: Missing reason must be rejected.");
            Assert((await cdT1Mgr.SupervisorOvernightClose(new SupervisorOvernightCloseRequestDto
            {
                TerminalId = 1,
                ShiftId = shiftNov20.ShiftId,
                Reason = "Missing count",
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>()
            })).Result is BadRequestObjectResult, "Scenario 7: Empty denominations must be rejected (no fake count).");

            // Scenarios 7, 8, 9: Supervisor enters physical denomination count = ₱1,450.00 (₱50.00 SHORT) & confirms
            var recoveryReq = new SupervisorOvernightCloseRequestDto
            {
                ClientOperationId = "ovn01-recovery-op-1",
                ShiftId = shiftNov20.ShiftId,
                ClientShiftId = shiftNov20.ClientShiftId,
                TerminalId = 1,
                BranchId = 1,
                IsDeclarationConfirmed = true,
                Reason = "Cashier A left shift open on Nov 20; physical cash verified by manager at 8:00 AM",
                ClosedAtUtc = nov21MorningUtc.AddMinutes(5),
                Denominations =
                [
                    new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new CashDenominationLineDto { Type = "BILL", Denomination = 200m, Quantity = 2 },
                    new CashDenominationLineDto { Type = "BILL", Denomination = 50m, Quantity = 1 }
                ]
            };
            var closeRes = (await cdT1Mgr.SupervisorOvernightClose(recoveryReq)).Result as OkObjectResult;
            var recoveredShift = closeRes?.Value as CashDrawerShiftDto;
            Assert(recoveredShift != null &&
                   recoveredShift.Status == "CLOSED" &&
                   recoveredShift.CashierId == "cashier1" &&
                   recoveredShift.ClosedBySupervisorId == "manager1" &&
                   recoveredShift.BusinessDate == "2026-11-20" &&
                   recoveredShift.OutletId == 1 &&
                   recoveredShift.BranchId == 1 &&
                   recoveredShift.TerminalId == 1 &&
                   recoveredShift.ExpectedBalance == 1500m &&
                   recoveredShift.ActualCountedBalance == 1450m &&
                   recoveredShift.Discrepancy == -50m &&
                   recoveredShift.ClosingType == TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery &&
                   recoveredShift.RecoveryStatus == TerminalClosingLockDto.RecoveryStatusResolved &&
                   recoveredShift.RecoveryReason == recoveryReq.Reason &&
                   recoveredShift.ClosedAt.HasValue &&
                   recoveredShift.Denominations.Sum(d => d.Subtotal) == 1450m,
                $"Scenarios 8-9: Supervisor overnight close must record exact audit fields, -₱50 shortage, and preserve original Business Date 2026-11-20. Got Status={recoveredShift?.Status}, BizDate={recoveredShift?.BusinessDate}, Disc={recoveredShift?.Discrepancy}");

            // Duplicate replay idempotency guard
            var replayClose = (await cdT1Mgr.SupervisorOvernightClose(recoveryReq)).Result as OkObjectResult;
            Assert(replayClose != null && ((CashDrawerShiftDto)replayClose.Value!).ShiftId == shiftNov20.ShiftId,
                "Idempotency: Replaying identical ClientOperationId must return existing closed shift without error.");
            Assert(await db.CashDrawerShifts.CountAsync(s => s.TerminalId == 1) == 1,
                "Idempotency: Zero duplicate CashDrawerShift rows allowed.");

            // Verify Cashier A restriction belongs strictly to original Business Date (2026-11-20), NOT 2026-11-21, and Terminal 1 is NOT locked!
            var restriction = await db.CashierClosingRestrictions.SingleAsync(r => r.TerminalId == 1 && r.CashierId == "cashier1");
            Assert(restriction.BusinessDate == "2026-11-20" && restriction.Status == "ACTIVE",
                $"Scenario 9: Restriction must be recorded for original Business Date 2026-11-20, got {restriction.BusinessDate}");
            var lockAfterRecovery = ((await cdT1B.GetTerminalLockStatus(1, "cashier2")).Result as OkObjectResult)?.Value as TerminalClosingLockDto;
            Assert(lockAfterRecovery != null &&
                   !lockAfterRecovery.HasUnresolvedPreviousDayShift &&
                   !lockAfterRecovery.IsShiftOwnedByAnotherCashier &&
                   !lockAfterRecovery.IsTerminalLocked &&
                   !lockAfterRecovery.IsCashierRestricted,
                "Scenarios 9-10: Terminal 1 must be cleanly released for Cashier B on Nov 21.");

            // Scenarios 10 & 11: Cashier B logs in and opens a new shift for Nov 21 with a separate opening fund (₱2,000)
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 10: Cashier B login on Terminal 1 for Nov 21 must succeed after supervisor recovery.");
            Assert((await sessionsB.ActivateSession(new CashierSessionActivateDto { BranchId = 1, TerminalId = 1, DeviceIdentifier = "uuid-ovn-t1" })) is OkObjectResult,
                "Scenario 10: Cashier B ActivateSession on Nov 21 must succeed.");
            var openBRes = await cdT1B.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                ClientShiftId = "ovn01-shift-nov21",
                OpeningFloat = 2000m,
                OpenedAt = nov21MorningUtc.AddMinutes(10)
            }) as OkObjectResult;
            var shiftNov21 = openBRes?.Value as CashDrawerShiftDto;
            Assert(shiftNov21 != null &&
                   shiftNov21.ShiftId != shiftNov20.ShiftId &&
                   shiftNov21.CashierId == "cashier2" &&
                   shiftNov21.OpeningFloat == 2000m &&
                   shiftNov21.BusinessDate == "2026-11-21" &&
                   shiftNov21.Status == "OPEN",
                "Scenario 11: Cashier B's Nov 21 shift must record its own separate ₱2,000 opening fund and Business Date 2026-11-21.");

            // Scenario 12: Cashier A's Nov 20 transactions & movements remain completely untouched
            var preservedOrder = await db.Orders.AsNoTracking().SingleAsync(o => o.OrderId == nov20Order.OrderId);
            Assert(preservedOrder.CashierId == "cashier1" &&
                   preservedOrder.ShiftId == shiftNov20.ShiftId &&
                   preservedOrder.TotalAmount == 350m &&
                   preservedOrder.InvoiceNumber == "INV-0000001",
                "Scenario 12: Cashier A's original Nov 20 order must remain completely untouched.");

            // Scenario 17: Z-Reading for Nov 20 is now READY and generates cleanly with Cashier A's shift & variance!
            var postReadinessNov20 = await zSvc.ReadinessAsync(1, nov20);
            Assert(postReadinessNov20.IsReady,
                $"Scenario 17: Z-Reading readiness for Nov 20 must succeed after supervisor overnight closing. Blockers: {string.Join("; ", postReadinessNov20.Blockers)}");

            var zController = CreateTerminalScopedZController(db, cfg, "manager1", "Manager", terminalId: 1);
            var zNov20Res = (await zController.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = nov20,
                ClientOperationId = "ovn01-z-nov20",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            })) as OkObjectResult;
            Assert(zNov20Res != null, "Scenario 17: Finalizing Z-Reading for Nov 20 after overnight recovery must succeed.");
            var zNov20Report = (ZReadingReportDto)zNov20Res!.Value!;
            Assert(zNov20Report.GrossSales == 350m &&
                   zNov20Report.Shifts.Count == 1 &&
                   zNov20Report.Shifts[0].CashierId == "cashier1" &&
                   zNov20Report.Shifts[0].Discrepancy == -50m,
                "Scenario 17: Nov 20 Z-Reading must preserve Cashier A's sales and -₱50 overnight discrepancy.");

            // Finalizing previous-day Nov 20 Z-Reading must NOT lock Terminal 1 for current Business Date Nov 21!
            var lockAfterNov20Z = ((await cdT1B.GetTerminalLockStatus(1, "cashier2")).Result as OkObjectResult)?.Value as TerminalClosingLockDto;
            Assert(lockAfterNov20Z != null && !lockAfterNov20Z.IsTerminalLocked,
                "Scenario 17: Finalizing prior-day Nov 20 Z-Reading must NOT lock Terminal 1 for Nov 21.");

            // Close Cashier B's Nov 21 shift and finalize Nov 21 Z-Reading -> Terminal 1 locks genuinely for Nov 21 (423)!
            BusinessDayClock.UtcNowProvider = () => nov21MorningUtc.AddHours(6);
            var closeB = await cdT1B.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftNov21!.ShiftId,
                IsDeclarationConfirmed = true,
                Denominations = [new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 2 }]
            });
            Assert(closeB is OkObjectResult, "Scenario 17: Cashier B closing Nov 21 shift must succeed.");

            var zNov21Res = await zController.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = nov21,
                ClientOperationId = "ovn01-z-nov21",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            });
            Assert(zNov21Res is OkObjectResult, "Scenario 17: Finalizing Nov 21 Z-Reading must succeed once both Nov 20 (recovered) and Nov 21 shifts are closed.");
            var lockedT1 = await auth.PinLogin(new PinLoginDto { PinCode = "9999", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(lockedT1?.StatusCode == 423, $"Scenario 17: Genuine Nov 21 Z-Reading must lock Terminal 1 (423), got {lockedT1?.StatusCode}");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenario 13: Actual cash cannot be verified -> shift remains OPEN & flagged for investigation (NO fake count,
    // NOT marked balanced/closed, unauthorized cashier still blocked, Z-Reading still blocked), then resolved later once verified!
    private static async Task TestOvernightRecovery02_UnverifiedCashCannotBeForcedStaysOpenAndBlocksTakeoverAndZReading()
    {
        try
        {
            var nov20 = new DateOnly(2026, 11, 20);
            var nov20Utc = new DateTime(2026, 11, 20, 7, 0, 0, DateTimeKind.Utc);
            var nov21Utc = new DateTime(2026, 11, 21, 0, 15, 0, DateTimeKind.Utc); // Nov 21 8:15 AM Manila
            BusinessDayClock.UtcNowProvider = () => nov20Utc;

            var (conn, db, seededShift) = await CreateTestEnvAsync();
            await using var _ = conn;

            db.CashDrawerShifts.Remove(seededShift);
            db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = nov20, ConfiguredBy = "admin" });
            await db.SaveChangesAsync();

            var cfg = BuildWorkflowJwtConfig();
            var auth = new AuthController(db, cfg);
            var cdT1A = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 1);
            var cdT1B = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);
            var cdT1Mgr = CreateTerminalScopedCashDrawerController(db, "manager1", "Manager", terminalId: 1);

            var openA = (await cdT1A.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                ClientShiftId = "ovn02-shift-nov20",
                OpeningFloat = 1000m,
                OpenedAt = nov20Utc
            }) as OkObjectResult)!.Value as CashDrawerShiftDto;
            var ent = await db.CashDrawerShifts.SingleAsync(s => s.ShiftId == openA!.ShiftId);
            ent.TotalCashSales = 500m;
            ent.ExpectedBalance = 1500m;
            await db.SaveChangesAsync();

            // Advance past 4:00 AM to Nov 21
            BusinessDayClock.UtcNowProvider = () => nov21Utc;

            // Missing reason rejected
            Assert((await cdT1Mgr.FlagUnverifiedCash(new SupervisorUnverifiedCashRequestDto
            {
                TerminalId = 1,
                ShiftId = openA!.ShiftId,
                Reason = ""
            })).Result is BadRequestObjectResult, "Scenario 13: Investigation reason must be required.");

            // Unauthorized cashier rejected
            Assert((await cdT1B.FlagUnverifiedCash(new SupervisorUnverifiedCashRequestDto
            {
                TerminalId = 1,
                ShiftId = openA.ShiftId,
                Reason = "Unauthorized flag"
            })).Result is ForbidResult, "Scenario 13: Regular cashier must be forbidden from flagging unverified cash.");

            // Supervisor records unverified cash for investigation
            var flagReq = new SupervisorUnverifiedCashRequestDto
            {
                ClientOperationId = "ovn02-unver-1",
                TerminalId = 1,
                BranchId = 1,
                ShiftId = openA.ShiftId,
                Reason = "Cash bag sealed in drop safe; awaiting branch manager key to verify count",
                ReportedAtUtc = nov21Utc
            };
            var flagDto = ((await cdT1Mgr.FlagUnverifiedCash(flagReq)).Result as OkObjectResult)?.Value as CashDrawerShiftDto;
            Assert(flagDto != null &&
                   flagDto.Status == "OPEN" &&
                   flagDto.ActualCountedBalance == null &&
                   flagDto.Discrepancy == null &&
                   flagDto.ClosedAt == null &&
                   flagDto.RecoveryStatus == TerminalClosingLockDto.RecoveryStatusUnverifiedCash &&
                   flagDto.UnverifiedCashReason == flagReq.Reason &&
                   flagDto.UnverifiedCashReportedBy == "manager1" &&
                   flagDto.UnverifiedCashReportedAtUtc.HasValue,
                "Scenario 13: Unverified cash must keep shift OPEN, null count/discrepancy, and record investigation details.");

            // Replay idempotency
            var flagReplay = ((await cdT1Mgr.FlagUnverifiedCash(flagReq)).Result as OkObjectResult)?.Value as CashDrawerShiftDto;
            Assert(flagReplay != null && flagReplay.Status == "OPEN", "Scenario 13: Unverified cash flag replay must be idempotent.");

            // Lock status clearly exposes investigation state; Cashier B STILL blocked; Z-Reading STILL blocked!
            var lockDto = ((await cdT1B.GetTerminalLockStatus(1, "cashier2")).Result as OkObjectResult)?.Value as TerminalClosingLockDto;
            Assert(lockDto != null &&
                   lockDto.HasUnresolvedPreviousDayShift &&
                   lockDto.UnresolvedShiftRecoveryStatus == TerminalClosingLockDto.RecoveryStatusUnverifiedCash &&
                   lockDto.UnresolvedShiftInvestigationReason == flagReq.Reason &&
                   lockDto.UnresolvedShiftInvestigationBy == "manager1" &&
                   lockDto.IsShiftOwnedByAnotherCashier,
                "Scenario 13: Lock status must expose UNVERIFIED_CASH_INVESTIGATION state and keep blocking Cashier B.");

            var bBlocked = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(bBlocked?.StatusCode == 409, "Scenario 13: Unauthorized Cashier B must remain blocked while cash is unverified.");
            Assert(await cdT1B.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier2", OpeningFloat = 1000m }) is ConflictObjectResult,
                "Scenario 13: Cashier B must not open a new shift while prior cash is unverified.");

            var zReadiness = await new ZReadingService(db, cfg).ReadinessAsync(1, nov20);
            Assert(!zReadiness.IsReady && zReadiness.Blockers.Any(b => b.Contains("unverified cash investigation", StringComparison.OrdinalIgnoreCase)),
                "Scenario 13: Z-Reading must stay blocked with explicit unverified-cash investigation blocker.");

            // Later: physical cash verified (₱1,550.00 = +₱50 OVER) -> resolves cleanly while keeping investigation history!
            BusinessDayClock.UtcNowProvider = () => nov21Utc.AddHours(2);
            var resolveDto = ((await cdT1Mgr.SupervisorOvernightClose(new SupervisorOvernightCloseRequestDto
            {
                ClientOperationId = "ovn02-resolve-1",
                TerminalId = 1,
                BranchId = 1,
                ShiftId = openA.ShiftId,
                IsDeclarationConfirmed = true,
                Reason = "Safe opened with branch manager; physical cash verified",
                ClosedAtUtc = nov21Utc.AddHours(2),
                Denominations =
                [
                    new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new CashDenominationLineDto { Type = "BILL", Denomination = 500m, Quantity = 1 },
                    new CashDenominationLineDto { Type = "BILL", Denomination = 50m, Quantity = 1 }
                ]
            })).Result as OkObjectResult)?.Value as CashDrawerShiftDto;
            Assert(resolveDto != null &&
                   resolveDto.Status == "CLOSED" &&
                   resolveDto.Discrepancy == 50m &&
                   resolveDto.RecoveryStatus == TerminalClosingLockDto.RecoveryStatusResolved &&
                   resolveDto.UnverifiedCashReason == flagReq.Reason &&
                   resolveDto.BusinessDate == "2026-11-20",
                "Scenario 13: Subsequent verified close must record +₱50 overage, preserve original Business Date & prior investigation note.");
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Scenario 13: Cashier B can now log in cleanly after verified recovery closing.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenarios 14 & 15 + Section 7 (Offline-First E2E: Offline Detection after 4:00 AM, App Restart Persistence,
    // Offline Unverified Cash + Offline Supervisor Recovery with SHA-256 PinHash [zero plaintext PIN],
    // Stale Server Snapshot Protection, Offline Nov 21 Fresh Shift, and Causal Idempotent Reconnect Sync!)
    private static async Task TestOvernightRecovery03_OfflineDetectionRestartRecoveryStaleSnapshotProtectionAndIdempotentSync()
    {
        try
        {
            var nov20Utc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc); // Nov 20 2:00 PM Manila
            var nov21Utc = new DateTime(2026, 11, 21, 0, 30, 0, DateTimeKind.Utc); // Nov 21 8:30 AM Manila
            using var localScope = new TempLocalDbScope("ovn03_offline");
            SetDeterministicManilaClock(nov20Utc, tickCount64: 500_000L);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);

            using (var seedDb = new LocalPosDbContext())
            {
                seedDb.InitializeDatabase();
                Assert(LocalDatabaseMigrator.GetUserVersion(seedDb.Database.GetDbConnection()) == LocalDatabaseMigrator.CurrentSchemaVersion,
                    $"SQLite schema must migrate to version {LocalDatabaseMigrator.CurrentSchemaVersion}.");

                string HashPin(string p) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p)));
                seedDb.Cashiers.RemoveRange(seedDb.Cashiers);
                seedDb.Cashiers.AddRange(
                    new LocalCashier { UserId = 1, Username = "cashier1", Role = "Cashier", PinHash = HashPin("1111"), SyncedAtUtc = nov20Utc },
                    new LocalCashier { UserId = 2, Username = "cashier2", Role = "Cashier", PinHash = HashPin("2222"), SyncedAtUtc = nov20Utc },
                    new LocalCashier { UserId = 3, Username = "manager1", Role = "Manager", PinHash = HashPin("9999"), SyncedAtUtc = nov20Utc });
                await seedDb.SaveChangesAsync();
            }

            using var noNetHttp = new HttpClient(new InProcSyncHandler(_ => throw new HttpRequestException("Offline")))
            {
                BaseAddress = new Uri("http://localhost/")
            };

            // 1. Offline on Nov 20: Cashier A opens shift (₱1,000), sells ₱224 cash (Expected ₱1,224), logs out without closing
            var shiftA = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = "ovn03-shift-nov20",
                OpeningFloat = 1000m,
                OpenedAt = nov20Utc
            }, "cashier1", offline: true);
            Assert(shiftA.BusinessDate == "2026-11-20", $"Offline Nov 20 shift must record BusinessDate 2026-11-20, got {shiftA.BusinessDate}");

            var sessionA = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using (var checkoutDb = new LocalPosDbContext())
            {
                var sale = await new OfflineCheckoutService(checkoutDb, sessionA).ProcessSaleAsync(
                    BuildSingleCashOrderDto(shiftA.ShiftId, shiftA.ClientShiftId!, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(sale.Success, $"Offline Nov 20 cash sale failed: {sale.ErrorMessage}");
            }
            sessionA.ClearSession();

            // 2. Advance past 4:00 AM to Nov 21 + simulate full APP RESTART (fresh PosSessionService, fresh DbContext, 100% offline!)
            SetDeterministicManilaClock(nov21Utc, tickCount64: 500_000L + 66_600_000L);

            var lockBOffline = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(lockBOffline.HasUnresolvedPreviousDayShift &&
                   lockBOffline.UnresolvedShiftBusinessDate == "2026-11-20" &&
                   lockBOffline.UnresolvedShiftCashierId == "cashier1" &&
                   lockBOffline.IsShiftOwnedByAnotherCashier &&
                   lockBOffline.UnresolvedShiftMessage!.Contains("2026-11-20"),
                "Scenario 14: Offline lock evaluation after restart must detect unresolved Nov 20 shift without internet.");

            // Offline checkout blocked even if Cashier A tries to ring up a sale on Nov 21
            var restartedSessionA = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using (var blockedCheckoutDb = new LocalPosDbContext())
            {
                var blockedSale = await new OfflineCheckoutService(blockedCheckoutDb, restartedSessionA).ProcessSaleAsync(
                    BuildSingleCashOrderDto(shiftA.ShiftId, shiftA.ClientShiftId!, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(!blockedSale.Success && blockedSale.ErrorMessage!.Contains("2026-11-20"),
                    "Scenario 14: OfflineCheckoutService must block new Nov 21 sales under unresolved Nov 20 shift.");
            }

            // Offline inspect returns full unredacted expected cash (₱1,224)
            var offlineInspect = await ShiftSession.GetUnresolvedPreviousDayShiftAsync(noNetHttp, 1, offline: true);
            Assert(offlineInspect != null &&
                   offlineInspect.CashierId == "cashier1" &&
                   offlineInspect.BusinessDate == "2026-11-20" &&
                   offlineInspect.ExpectedBalance == 1224m &&
                   offlineInspect.IsUnresolvedPreviousBusinessDate,
                $"Scenario 14: Offline inspect must return expected cash ₱1,224.00 and Nov 20 date. Got Expected={offlineInspect?.ExpectedBalance}");

            // Security: Cashier PIN rejected offline
            bool cashierPinRejected = false;
            try
            {
                await ShiftSession.FlagUnverifiedCashAsync(noNetHttp, new SupervisorUnverifiedCashRequestDto
                {
                    TerminalId = 1,
                    BranchId = 1,
                    ShiftId = shiftA.ShiftId,
                    ClientShiftId = shiftA.ClientShiftId,
                    Reason = "Cashier spoof",
                    SupervisorPin = "2222"
                }, "cashier2", "Cashier", offline: true);
            }
            catch (InvalidOperationException) { cashierPinRejected = true; }
            Assert(cashierPinRejected, "Security: Non-supervisor PIN must be rejected offline.");

            // 3. Offline Supervisor flags unverified cash -> shift stays OPEN, outbox queued WITHOUT plaintext PIN!
            var offlineUnver = await ShiftSession.FlagUnverifiedCashAsync(noNetHttp, new SupervisorUnverifiedCashRequestDto
            {
                ClientOperationId = "ovn03-unver-1",
                TerminalId = 1,
                BranchId = 1,
                ShiftId = shiftA.ShiftId,
                ClientShiftId = shiftA.ClientShiftId,
                Reason = "Offline cash count pending safe unlock",
                SupervisorPin = "9999",
                ReportedAtUtc = nov21Utc
            }, "manager1", "Manager", offline: true);
            Assert(offlineUnver.Status == "OPEN" &&
                   offlineUnver.RecoveryStatus == TerminalClosingLockDto.RecoveryStatusUnverifiedCash &&
                   offlineUnver.ActualCountedBalance == null,
                "Scenario 14: Offline unverified cash flag must keep shift OPEN without fake count.");

            using (var checkOutboxDb = new LocalPosDbContext())
            {
                var unverOutbox = await checkOutboxDb.SyncOutboxes.SingleAsync(o => o.EntityType == "SHIFT_UNVERIFIED_CASH");
                Assert(!unverOutbox.PayloadJson.Contains("\"9999\""), "Security: Plaintext supervisor PIN must NEVER be persisted in SQLite outbox.");
                Assert(unverOutbox.PayloadJson.Contains("SupervisorPinHash"), "Offline recovery outbox must store cryptographic SupervisorPinHash.");
            }

            // 4. Offline Supervisor completes verified overnight recovery close (Counted ₱1,200 vs Expected ₱1,224 -> -₱24 SHORT)
            var offlineClose = await ShiftSession.SupervisorOvernightCloseAsync(noNetHttp, new SupervisorOvernightCloseRequestDto
            {
                ClientOperationId = "ovn03-close-1",
                TerminalId = 1,
                BranchId = 1,
                ShiftId = shiftA.ShiftId,
                ClientShiftId = shiftA.ClientShiftId,
                IsDeclarationConfirmed = true,
                Reason = "Verified physical cash offline at 8:45 AM",
                SupervisorPin = "9999",
                ClosedAtUtc = nov21Utc.AddMinutes(15),
                Denominations =
                [
                    new CashDenominationLineDto { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new CashDenominationLineDto { Type = "BILL", Denomination = 200m, Quantity = 1 }
                ]
            }, "manager1", "Manager", offline: true);
            Assert(offlineClose.Status == "CLOSED" &&
                   offlineClose.BusinessDate == "2026-11-20" &&
                   offlineClose.ClosingType == TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery &&
                   offlineClose.ClosedBySupervisorId == "manager1" &&
                   offlineClose.ActualCountedBalance == 1200m &&
                   offlineClose.Discrepancy == -24m,
                $"Scenario 14: Offline supervisor overnight close must record -₱24 shortage and preserve Nov 20 date. Got Disc={offlineClose.Discrepancy}");

            // Stale server OPEN snapshot protection!
            await ShiftSession.CacheAsync(new CashDrawerShiftDto
            {
                ShiftId = 999,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                ClientShiftId = "ovn03-shift-nov20",
                BusinessDate = "2026-11-20",
                OpeningFloat = 1000m,
                OpeningConfirmed = true,
                ExpectedBalance = 1000m,
                Status = "OPEN",
                OpenedAt = nov20Utc,
                AcknowledgedLocalTransactionIds = new List<string>()
            }, reconcileCurrent: false);
            using (var staleCheckDb = new LocalPosDbContext())
            {
                var mergedRow = await staleCheckDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == 999);
                Assert(mergedRow.Status == "CLOSED" &&
                       mergedRow.ClosingType == TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery &&
                       mergedRow.ActualCountedBalance == 1200m &&
                       mergedRow.ClientCloseId == "ovn03-close-1",
                    "Section 7: Stale server OPEN snapshot must NEVER overwrite a newer local offline supervisor recovery close!");
                // Clean up synthetic positive probe row so outbox sync tests real ShiftId resolution from negative -> server ShiftId
                staleCheckDb.CashDrawerShifts.Remove(await staleCheckDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == 999));
                await staleCheckDb.SaveChangesAsync();
            }

            // Lock status offline on Nov 21 now allows Cashier B to open a fresh Nov 21 shift with separate ₱2,000 fund!
            var lockBAfterOfflineRecovery = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(!lockBAfterOfflineRecovery.HasUnresolvedPreviousDayShift &&
                   !lockBAfterOfflineRecovery.IsShiftOwnedByAnotherCashier &&
                   !lockBAfterOfflineRecovery.IsTerminalLocked,
                "Scenario 14: Terminal 1 must be released offline for Cashier B after supervisor overnight recovery.");

            var shiftB = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = "ovn03-shift-nov21",
                OpeningFloat = 2000m,
                OpenedAt = nov21Utc.AddMinutes(20)
            }, "cashier2", offline: true);
            Assert(shiftB.BusinessDate == "2026-11-21" && shiftB.OpeningFloat == 2000m,
                "Scenario 14: Cashier B must open a separate offline Nov 21 shift with ₱2,000 float.");

            // 5. Scenario 15: Reconnect & causally sync ALL queued items (including Nov 21 SHIFT_OPEN AFTER Nov 20 SHIFT_OVERNIGHT_CLOSE!)
            var (srvConn, srvDb, srvSeedShift) = await CreateTestEnvAsync();
            await using var _srv = srvConn;
            var srvTerm = await srvDb.Terminals.SingleAsync(t => t.TerminalId == 1);
            srvTerm.DeviceIdentifier = "DEV-UUID-1";
            srvTerm.Uuid = "DEV-UUID-1";
            srvSeedShift.Status = "CLOSED";
            srvSeedShift.OpenedAt = nov20Utc.AddDays(-2);
            srvSeedShift.ClosedAt = nov20Utc.AddDays(-2).AddHours(4);
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
                if (path.EndsWith("/api/cash-drawer/overnight-recovery-unverified"))
                    return ActionResultToHttpResponse(await srvCashDrawer.FlagUnverifiedCash((await req.Content!.ReadFromJsonAsync<SupervisorUnverifiedCashRequestDto>())!));
                if (path.EndsWith("/api/cash-drawer/overnight-recovery-close"))
                    return ActionResultToHttpResponse(await srvCashDrawer.SupervisorOvernightClose((await req.Content!.ReadFromJsonAsync<SupervisorOvernightCloseRequestDto>())!));
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
            Assert(pending.Count == 5,
                $"Scenario 15: Expected 5 queued outbox items (SHIFT_OPEN Nov20, Order, SHIFT_UNVERIFIED_CASH, SHIFT_OVERNIGHT_CLOSE, SHIFT_OPEN Nov21), got {pending.Count}.");

            foreach (var item in pending)
            {
                bool ok = await syncSvc.UploadTransactionAsync(item, syncHttp);
                if (!ok)
                {
                    using var errDb = new LocalPosDbContext();
                    var failedRow = await errDb.SyncOutboxes.AsNoTracking().SingleOrDefaultAsync(o => o.OutboxId == item.OutboxId);
                    Assert(false, $"Scenario 15: Sync failed for {item.EntityType}/{item.EntityId}: {failedRow?.LastError}");
                }
            }

            // Explicit duplicate replay against server endpoints (must be 100% idempotent, ZERO duplicates!)
            var rawClosePayload = JsonSerializer.Deserialize<SupervisorOvernightCloseRequestDto>(
                pending.Single(p => p.EntityType == "SHIFT_OVERNIGHT_CLOSE").PayloadJson)!;
            var dupCloseRes = (await srvCashDrawer.SupervisorOvernightClose(rawClosePayload)).Result as OkObjectResult;
            Assert(dupCloseRes != null, "Scenario 15: Direct server replay of SHIFT_OVERNIGHT_CLOSE must be idempotent (200 OK).");

            var srvShiftNov20 = await srvDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ClientShiftId == "ovn03-shift-nov20");
            var srvShiftNov21 = await srvDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ClientShiftId == "ovn03-shift-nov21");
            Assert(srvShiftNov20.Status == "CLOSED" &&
                   srvShiftNov20.CashierId == "cashier1" &&
                   srvShiftNov20.BusinessDate == "2026-11-20" &&
                   srvShiftNov20.ClosingType == TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery &&
                   srvShiftNov20.ClosedBySupervisorId == "manager1" &&
                   srvShiftNov20.ExpectedBalance == 1224m &&
                   srvShiftNov20.ActualCountedBalance == 1200m &&
                   srvShiftNov20.Discrepancy == -24m &&
                   srvShiftNov20.UnverifiedCashReason == "Offline cash count pending safe unlock",
                $"Scenario 15: Server Nov 20 shift must carry exact expected ₱1,224, counted ₱1,200, discrepancy -₱24, and audit fields. Got Exp={srvShiftNov20.ExpectedBalance}, Act={srvShiftNov20.ActualCountedBalance}, Disc={srvShiftNov20.Discrepancy}");
            Assert(srvShiftNov21.Status == "OPEN" &&
                   srvShiftNov21.CashierId == "cashier2" &&
                   srvShiftNov21.BusinessDate == "2026-11-21" &&
                   srvShiftNov21.OpeningFloat == 2000m,
                "Scenario 15: Server Nov 21 shift must be cleanly opened for Cashier B with ₱2,000 float.");
            Assert(await srvDb.CashDrawerShifts.CountAsync(s => s.ClientShiftId == "ovn03-shift-nov20") == 1 &&
                   await srvDb.CashDrawerShifts.CountAsync(s => s.ClientShiftId == "ovn03-shift-nov21") == 1 &&
                   await srvDb.Orders.CountAsync(o => o.TerminalId == 1) == 1 &&
                   await srvDb.CashierClosingRestrictions.CountAsync(r => r.TerminalId == 1 && r.CashierId == "cashier1" && r.BusinessDate == "2026-11-20") == 1,
                "Scenario 15: Reconnect sync must produce zero duplicate shifts, orders, or restrictions.");

            var autoProvisionedState = await srvDb.ZReadingTerminalStates.AsNoTracking().SingleOrDefaultAsync(z => z.TerminalId == 1);
            Assert(autoProvisionedState != null &&
                   autoProvisionedState.FirstBusinessDate == new DateOnly(2026, 11, 18) &&
                   autoProvisionedState.OpeningAccumulatedSales == 0m,
                $"Z-Reading baseline must be auto-provisioned with earliest shift Business Date (2026-11-18) and 0.00 opening sales. Got: {autoProvisionedState?.FirstBusinessDate}");

            var nov20LocalBlockers = await ZReadingLocalReadiness.GetBlockersAsync(syncDb, 1, new DateOnly(2026, 11, 20));
            Assert(!nov20LocalBlockers.Any(b => b.Contains("missing required cash accountability", StringComparison.OrdinalIgnoreCase)),
                $"Retired negative shift placeholder must not false-trigger missing cash accountability blocker. Got: {string.Join("; ", nov20LocalBlockers)}");

            syncDb.CashDrawerShifts.Add(new LocalCashDrawerShift
            {
                ShiftId = -999999,
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                CashierId = "cashier1",
                BusinessDate = "2026-11-20",
                Status = "CLOSED",
                ClientShiftId = "uncounted-local-shift",
                ClientCloseId = "uncounted-local-close",
                OpenedAt = shiftA.OpenedAt,
                ClosedAt = shiftA.OpenedAt.AddHours(2),
                ActualCountedBalance = null
            });
            await syncDb.SaveChangesAsync();

            var nov20WithUncounted = await ZReadingLocalReadiness.GetBlockersAsync(syncDb, 1, new DateOnly(2026, 11, 20));
            Assert(nov20WithUncounted.Any(b => b.Contains("missing required cash accountability", StringComparison.OrdinalIgnoreCase)),
                "Genuinely uncounted local close on target Business Date must still block Z-Reading.");

            var nov21IgnoringNov20Row = await ZReadingLocalReadiness.GetBlockersAsync(syncDb, 1, new DateOnly(2026, 11, 21));
            Assert(!nov21IgnoringNov20Row.Any(b => b.Contains("missing required cash accountability", StringComparison.OrdinalIgnoreCase)),
                "Closed local row from prior Business Date (2026-11-20) must not false-block 2026-11-21 local readiness check.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }
}
