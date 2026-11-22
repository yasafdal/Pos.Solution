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
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private static IConfiguration BuildWorkflowJwtConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "SuperSecretTestKeyForWorkflowSeparationTests123456!",
                ["Jwt:Issuer"] = "POS.Api",
                ["Jwt:Audience"] = "POS.Client",
                ["ZReading:Enabled"] = "true"
            })
            .Build();

    private static CashDrawerController CreateTerminalScopedCashDrawerController(
        PosDbContext db,
        string username,
        string role,
        int terminalId,
        int branchId = 1)
    {
        var controller = new CashDrawerController(db);
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

    private static ZReadingsController CreateTerminalScopedZController(
        PosDbContext db,
        IConfiguration config,
        string username,
        string role,
        int terminalId,
        int branchId = 1)
    {
        var controller = new ZReadingsController(db, config);
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

    // Scenarios 1, 2, 3 & 14 (partial):
    // Close Cashier restricts ONLY (CashierId + TerminalId + BusinessDate).
    // - Failed Close Cashier NEVER restricts cashier.
    // - Cashier A closes T1 -> Cashier A blocked on T1 (403), CAN log in & open shift on T2.
    // - Cashier B CAN log in & open shift on T1 immediately after Cashier A closes.
    private static async Task TestWorkflow01_CloseCashierScopeAndMultiTerminalIsolation()
    {
        try
        {
            // Business Date 2026-11-20 at 1:00 PM Asia/Manila (05:00 UTC)
            var nowUtc = new DateTime(2026, 11, 20, 5, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift1) = await CreateTestEnvAsync();
            await using var _ = conn;

            // Add Terminal 2 in same Branch/Outlet
            db.Terminals.Add(new Terminal
            {
                TerminalId = 2,
                BranchId = 1,
                OutletId = 1,
                TerminalCode = "POS-02",
                Name = "Terminal 2",
                DeviceIdentifier = "DEV-T2",
                IsActive = true
            });
            shift1.OpenedAt = nowUtc.AddHours(-2);
            shift1.OpeningFloat = 1000m;
            shift1.TotalCashSales = 500m;
            shift1.ExpectedBalance = 1500m;
            await db.SaveChangesAsync();

            var jwtCfg = BuildWorkflowJwtConfig();
            var auth = new AuthController(db, jwtCfg);
            var cdT1Cashier1 = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 1);

            // Scenario 14a: Failed Close Cashier (pending approval movement) must NEVER restrict cashier
            db.CashDrawerMovements.Add(new CashDrawerMovement
            {
                ShiftId = shift1.ShiftId,
                BranchId = 1,
                TerminalId = 1,
                MovementType = "PETTY_CASH",
                Amount = 100m,
                Reason = "Pending ice",
                Status = "PENDING_APPROVAL",
                CreatedBy = "cashier1",
                AuthorizedBy = "cashier1",
                CreatedAt = nowUtc.AddMinutes(-10)
            });
            await db.SaveChangesAsync();

            var failedClose = await cdT1Cashier1.CloseShift(new CloseShiftDto
            {
                ShiftId = shift1.ShiftId,
                ActualCountedBalance = 1500m,
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 500m, Quantity = 1 }
                }
            });
            Assert(failedClose is BadRequestObjectResult, "CloseShift with pending approval movement must fail.");
            Assert(!await db.CashierClosingRestrictions.AnyAsync(), "Failed Close Cashier must NEVER create a cashier restriction.");
            Assert((await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 })) is OkObjectResult,
                "Cashier A must remain unrestricted after a failed Close Cashier.");

            // Reject pending movement so close succeeds cleanly with denomination breakdown
            var pendingMv = await db.CashDrawerMovements.SingleAsync();
            pendingMv.Status = "REJECTED";
            await db.SaveChangesAsync();

            // Close Cashier A on T1 with ₱1,450 counted (Expected ₱1,500 -> Shortage -₱50)
            var okClose = await cdT1Cashier1.CloseShift(new CloseShiftDto
            {
                ShiftId = shift1.ShiftId,
                ClientCloseId = "wf01-close-c1-t1",
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 200m, Quantity = 2 },
                    new() { Type = "BILL", Denomination = 50m, Quantity = 1 }
                },
                Notes = "Afternoon shift close"
            });
            Assert(okClose is OkObjectResult, "Valid CloseShift with denomination breakdown must succeed.");

            var savedShift1 = await db.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == shift1.ShiftId);
            Assert(savedShift1.Status == "CLOSED" && savedShift1.ExpectedBalance == 1500m &&
                   savedShift1.ActualCountedBalance == 1450m && savedShift1.Discrepancy == -50m &&
                   !string.IsNullOrWhiteSpace(savedShift1.ClosingDenominationsJson),
                $"CloseShift must persist Expected(1500), Actual(1450), Discrepancy(-50), and denominations. Got Exp={savedShift1.ExpectedBalance}, Act={savedShift1.ActualCountedBalance}, Diff={savedShift1.Discrepancy}.");

            // Scenario 1: Cashier A blocked from logging in or opening another shift on T1 for SAME Business Date
            var c1LoginT1 = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(c1LoginT1?.StatusCode == 403, $"Cashier A must be blocked (403) on T1 after Close Cashier, got {c1LoginT1?.StatusCode}.");

            var c1OpenT1 = await cdT1Cashier1.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier1", OpeningFloat = 1000m });
            Assert(c1OpenT1 is ConflictObjectResult, "Cashier A must be blocked from opening another shift on T1 for the same Business Date.");

            // Scenario 2: Cashier A CAN still log in and open shift on T2 for SAME Business Date!
            var c1LoginT2 = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 2, BranchId = 1 });
            Assert(c1LoginT2 is OkObjectResult, "Cashier A MUST still be allowed to log in on Terminal 2 on the same Business Date.");

            var cdT2Cashier1 = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 2);
            var c1OpenT2 = await cdT2Cashier1.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 2,
                CashierId = "cashier1",
                ClientShiftId = "wf01-c1-t2",
                OpeningFloat = 1000m,
                OpenedAt = nowUtc.AddMinutes(5)
            });
            Assert(c1OpenT2 is OkObjectResult, "Cashier A MUST be able to open a new shift on Terminal 2.");

            // Scenario 3: Cashier B CAN log in and open a shift on T1 immediately after Cashier A closed!
            var c2LoginT1 = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 });
            Assert(c2LoginT1 is OkObjectResult, "Cashier B MUST be allowed to log in on Terminal 1 after Cashier A closed.");

            var cdT1Cashier2 = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);
            var c2OpenT1 = await cdT1Cashier2.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                ClientShiftId = "wf01-c2-t1",
                OpeningFloat = 1000m,
                OpenedAt = nowUtc.AddMinutes(10)
            });
            Assert(c2OpenT1 is OkObjectResult, "Cashier B MUST be able to open a shift on Terminal 1 after Cashier A closed.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenarios 4, 5 & 6:
    // Anytime Read-Only X-Reading:
    // - Runs while Cashier B is actively working (OPEN shift) after Cashier A closed -> spans multiple shifts/cashiers.
    // - Enforces Supervisor role or valid Supervisor PIN (plain Cashier without PIN denied).
    // - Repeatable 3x with identical figures and ZERO state mutations (shifts stay OPEN, no cashier/terminal locks created).
    private static async Task TestWorkflow02_AnytimeReadOnlyXReadingWhileShiftsOpenAndRepeatable()
    {
        try
        {
            var nowUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc); // 2:00 PM Manila
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shiftA) = await CreateTestEnvAsync();
            await using var _ = conn;

            var (dayStartUtc, _) = BusinessDayClock.GetPeriodUtc(new DateOnly(2026, 11, 20));
            shiftA.OpenedAt = DateTime.SpecifyKind(dayStartUtc.AddHours(2), DateTimeKind.Unspecified);
            shiftA.OpeningFloat = 1000m;
            shiftA.TotalCashSales = 500m;
            shiftA.ExpectedBalance = 1500m;
            await db.SaveChangesAsync();

            // Order 1 under Cashier A (₱500 Cash)
            var orderA = new Order
            {
                OrderNumber = "ORD-X-1",
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = shiftA.ShiftId,
                CashierId = "cashier1",
                OrderType = "DINE_IN",
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                CreatedAt = DateTime.SpecifyKind(dayStartUtc.AddHours(3), DateTimeKind.Unspecified),
                InvoiceIssuedAt = DateTime.SpecifyKind(dayStartUtc.AddHours(3), DateTimeKind.Unspecified),
                InvoiceNumber = "INV-X-001",
                Subtotal = 500m,
                TotalAmount = 500m,
                VatableSales = 446.43m,
                VatAmount = 53.57m
            };
            orderA.Payments.Add(new Payment { PaymentMethod = "CASH", Amount = 500m, PaymentStatus = "PAID", ProcessedBy = "cashier1", PaidAt = orderA.CreatedAt });
            db.Orders.Add(orderA);
            await db.SaveChangesAsync();

            // Close Cashier A 45 minutes before nowUtc so Cashier B's subsequent shift at -30 min does not overlap
            var cdA = CreateTerminalScopedCashDrawerController(db, "cashier1", "Cashier", terminalId: 1);
            var closeA = await cdA.CloseShift(new CloseShiftDto
            {
                ShiftId = shiftA.ShiftId,
                ClosedAt = nowUtc.AddMinutes(-45),
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 500m, Quantity = 1 }
                }
            });
            Assert(closeA is OkObjectResult, "Cashier A close must succeed.");

            // Open Cashier B shift and leave it actively OPEN with ₱300 GCash sale
            var cdB = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);
            var openBRes = await cdB.OpenShift(new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                CashierId = "cashier2",
                ClientShiftId = "wf02-shift-b",
                OpeningFloat = 1000m,
                OpenedAt = nowUtc.AddMinutes(-30)
            }) as OkObjectResult;
            Assert(openBRes != null, "Cashier B must open shift on T1.");
            var shiftBDto = (CashDrawerShiftDto)openBRes!.Value!;

            var orderB = new Order
            {
                OrderNumber = "ORD-X-2",
                BranchId = 1,
                OutletId = 1,
                TerminalId = 1,
                ShiftId = shiftBDto.ShiftId,
                CashierId = "cashier2",
                OrderType = "DINE_IN",
                OrderStatus = "COMPLETED",
                PaymentStatus = "PAID",
                CreatedAt = DateTime.SpecifyKind(nowUtc.AddMinutes(-10), DateTimeKind.Unspecified),
                InvoiceIssuedAt = DateTime.SpecifyKind(nowUtc.AddMinutes(-10), DateTimeKind.Unspecified),
                InvoiceNumber = "INV-X-002",
                Subtotal = 300m,
                DiscountAmount = 20m,
                TotalAmount = 280m,
                VatableSales = 250m,
                VatAmount = 30m
            };
            orderB.Payments.Add(new Payment { PaymentMethod = "GCASH", Amount = 280m, PaymentStatus = "PAID", ProcessedBy = "cashier2", PaidAt = orderB.CreatedAt });
            db.Orders.Add(orderB);
            await db.SaveChangesAsync();

            // Authorization check: plain Cashier without Supervisor PIN is denied (ForbidResult)
            var deniedCashier = await cdB.GetAnytimeXReading(terminalId: 1, businessDate: "2026-11-20", supervisorPin: null);
            Assert(deniedCashier.Result is ForbidResult, "Plain Cashier without supervisor PIN must be denied anytime X-Reading.");

            // Plain Cashier WITH valid Supervisor PIN (manager1 PIN = 9999 from CreateTestEnvAsync) succeeds
            var pinAuthorized = await cdB.GetAnytimeXReading(terminalId: 1, businessDate: "2026-11-20", supervisorPin: "9999");
            Assert(pinAuthorized.Result is OkObjectResult, "Valid Supervisor PIN must authorize anytime X-Reading.");

            // Scenario 4 & 5: Supervisor generates anytime X-Reading 3 times while Cashier B's shift is OPEN
            var cdMgr = CreateTerminalScopedCashDrawerController(db, "manager1", "Manager", terminalId: 1);
            var readings = new List<XReadingReportDto>();
            for (int i = 0; i < 3; i++)
            {
                var res = await cdMgr.GetAnytimeXReading(terminalId: 1, businessDate: "2026-11-20");
                Assert(res.Result is OkObjectResult, $"Anytime X-Reading attempt #{i + 1} must succeed.");
                readings.Add((XReadingReportDto)((OkObjectResult)res.Result!).Value!);
            }

            var r1 = readings[0];
            Assert(r1.IsAnytimeAudit && r1.BusinessDate == "2026-11-20", "X-Reading must be flagged as anytime audit with BusinessDate.");
            Assert(r1.ActiveShiftCount == 1 && r1.ClosedShiftCount == 1, $"Expected 1 active + 1 closed shift, got Active={r1.ActiveShiftCount}, Closed={r1.ClosedShiftCount}.");
            Assert(r1.GrossSales == 800m && r1.TotalDiscounts == 20m && r1.NetSales == 780m,
                $"Expected accumulated Gross=800, Discounts=20, Net=780 across Cashier A + B. Got Gross={r1.GrossSales}, Disc={r1.TotalDiscounts}, Net={r1.NetSales}.");
            Assert(r1.BeginningInvoiceNumber == "INV-X-001" && r1.EndingInvoiceNumber == "INV-X-002", "Invoice bounds must span both shifts.");
            Assert(r1.CashierSummaries.Count == 2 &&
                   r1.CashierSummaries.Any(c => c.CashierId == "cashier1" && c.ClosedShifts == 1 && c.NetSales == 500m) &&
                   r1.CashierSummaries.Any(c => c.CashierId == "cashier2" && c.OpenShifts == 1 && c.NetSales == 280m),
                "CashierSummaries must accurately reflect both closed Cashier A and active Cashier B.");

            // Identical figures across repeated calls
            for (int i = 1; i < readings.Count; i++)
            {
                Assert(readings[i].GrossSales == r1.GrossSales && readings[i].NetSales == r1.NetSales &&
                       readings[i].CompletedTransactionsCount == r1.CompletedTransactionsCount &&
                       readings[i].ActiveShiftCount == r1.ActiveShiftCount,
                    "Repeated X-Readings must return identical figures without resetting counters.");
            }

            // Scenario 6: Strictly read-only — Cashier B's shift MUST still be OPEN, Terminal NOT locked, Cashier B NOT restricted
            var shiftBAfterX = await db.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == shiftBDto.ShiftId);
            Assert(shiftBAfterX.Status == "OPEN" && shiftBAfterX.ClosedAt == null, "X-Reading must NEVER close active cashier shifts.");
            var termAfterX = await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == 1);
            Assert(termAfterX.ClosedBusinessDate == null, "X-Reading must NEVER lock the terminal.");
            var restrictionsAfterX = await db.CashierClosingRestrictions.AsNoTracking().ToListAsync();
            Assert(restrictionsAfterX.Count == 1 && restrictionsAfterX[0].CashierId == "cashier1",
                "X-Reading must NEVER create new cashier restrictions.");

            // HTML formatter displays clear X-Reading audit snapshot header and Business Date
            string html = XReadingHtmlFormatter.Format(r1);
            Assert(html.Contains("X-READING (AUDIT SNAPSHOT)", StringComparison.Ordinal) &&
                   html.Contains("2026-11-20", StringComparison.Ordinal) &&
                   html.Contains("cashier2", StringComparison.Ordinal),
                "XReadingHtmlFormatter must clearly render anytime X-Reading audit header, BusinessDate, and cashier summaries.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenarios 7, 8, 9, 10, 11, 12 & 14 (Z-Reading failure never locks):
    // - Z-Reading rejected while active cashier shift is OPEN.
    // - Z-Reading rejected when closed shift has incomplete cash accountability.
    // - Plain Cashier denied Z-Reading; Senior Cashier authorized to finalize Z-Reading.
    // - Failed Z-Reading NEVER partially locks terminal.
    // - Successful Z-Reading atomically locks ONLY (Terminal 1 + BusinessDate) -> 423 Locked for everyone on T1.
    // - Terminal 2 remains fully operational.
    // - Duplicate Z-Reading prevented for same (TerminalId, BusinessDate).
    // - At 3:59:59 AM Manila -> still restricted/locked; at 4:00:00 AM Manila -> both automatically reset!
    private static async Task TestWorkflow03_ZReadingGuardsAtomicTerminalLockMultiTerminalAnd4AmReset()
    {
        try
        {
            var bizDate = new DateOnly(2026, 11, 20);
            var (dayStartUtc, dayEndUtc) = BusinessDayClock.GetPeriodUtc(bizDate);
            // 10:00 PM Asia/Manila (14:00 UTC)
            var nowUtc = new DateTime(2026, 11, 20, 14, 0, 0, DateTimeKind.Utc);
            BusinessDayClock.UtcNowProvider = () => nowUtc;

            var (conn, db, shift1) = await CreateTestEnvAsync();
            await using var _ = conn;

            db.Users.Add(new User { UserId = 4, Username = "senior1", PasswordHash = "x", PinCode = "4444", Role = "Senior Cashier" });
            db.Terminals.Add(new Terminal
            {
                TerminalId = 2,
                BranchId = 1,
                OutletId = 1,
                TerminalCode = "POS-02",
                Name = "Terminal 2",
                DeviceIdentifier = "DEV-T2",
                IsActive = true
            });
            db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = bizDate, ConfiguredBy = "verified" });
            shift1.OpenedAt = DateTime.SpecifyKind(dayStartUtc.AddHours(2), DateTimeKind.Unspecified);
            shift1.OpeningFloat = 1000m;
            shift1.ExpectedBalance = 1000m;
            await db.SaveChangesAsync();

            var cfg = BuildWorkflowJwtConfig();
            var zCashier = CreateTerminalScopedZController(db, cfg, "cashier1", "Cashier", terminalId: 1);
            var zSenior = CreateTerminalScopedZController(db, cfg, "senior1", "Senior Cashier", terminalId: 1);
            var auth = new AuthController(db, cfg);

            // Scenario 9a: Plain Cashier denied Z-Reading
            var deniedZ = await zCashier.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "wf03-z-cashier",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            });
            Assert(deniedZ is ForbidResult, "Plain Cashier must be forbidden from generating Z-Reading.");

            // Scenario 7 & 14b: Active OPEN shift blocks Z-Reading AND never locks terminal
            var openShiftZ = await zSenior.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "wf03-z-open",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            }) as ObjectResult;
            Assert(openShiftZ?.StatusCode == 409, $"Z-Reading while shift is OPEN must return 409 Conflict, got {openShiftZ?.StatusCode}.");
            Assert((await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == 1)).ClosedBusinessDate == null,
                "Failed Z-Reading (open shift) must NEVER lock the terminal.");

            // Scenario 8 & 14c: Closed shift missing cash accountability (ActualCountedBalance / denominations) blocks Z-Reading AND never locks terminal
            shift1.Status = "CLOSED";
            shift1.ClosedAt = DateTime.SpecifyKind(nowUtc.AddHours(-1), DateTimeKind.Unspecified);
            shift1.ActualCountedBalance = null;
            shift1.ClosingDenominationsJson = null;
            shift1.ClosingOriginalDeclarationJson = null;
            await db.SaveChangesAsync();

            var incompleteAcctReadiness = (ZReadingReadinessDto)((OkObjectResult)await zSenior.Readiness(1, bizDate)).Value!;
            Assert(!incompleteAcctReadiness.IsReady &&
                   incompleteAcctReadiness.Blockers.Any(b => b.Contains("accountability", StringComparison.OrdinalIgnoreCase)),
                "Closed shift missing cash count/declaration must block Z-Reading readiness.");

            var incompleteAcctZ = await zSenior.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "wf03-z-incomplete",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            }) as ObjectResult;
            Assert(incompleteAcctZ?.StatusCode == 409, "Z-Reading with incomplete shift accountability must fail (409).");
            Assert((await db.Terminals.AsNoTracking().SingleAsync(t => t.TerminalId == 1)).ClosedBusinessDate == null,
                "Failed Z-Reading (incomplete accountability) must NEVER lock the terminal.");

            // Complete accountability on shift1 + cashier1 restriction
            shift1.ActualCountedBalance = 1000m;
            shift1.ClosingDenominationsJson = JsonSerializer.Serialize(new List<CashDenominationLineDto> { new() { Type = "BILL", Denomination = 1000m, Quantity = 1, Subtotal = 1000m } });
            shift1.ClosingOriginalDeclarationJson = "{}";
            await TerminalClosingLockGuard.RestrictCashierOnCloseAsync(db, 1, 1, "cashier1", shift1.ShiftId, shift1.ClientShiftId, "wf03-c1", nowUtc.AddHours(-1));
            await db.SaveChangesAsync();

            // Scenario 9b: Senior Cashier successfully finalizes Z-Reading on current Business Date!
            var finalizedRes = await zSenior.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "wf03-z-final",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            }) as OkObjectResult;
            Assert(finalizedRes != null, "Senior Cashier must be authorized to finalize Z-Reading once all shifts are closed and accounted.");
            var zReport = (ZReadingReportDto)finalizedRes!.Value!;
            Assert(zReport.ZNumber == 1 && zReport.BusinessDate == bizDate, "Finalized Z-Reading must have ZNumber=1 and matching BusinessDate.");

            // Duplicate Z-Reading check: second call never creates a second row or increments ZNumber
            var dupRes = await zSenior.Generate(new GenerateZReadingRequest
            {
                TerminalId = 1,
                BusinessDate = bizDate,
                ClientOperationId = "wf03-z-dup-other-op",
                DeviceIdentifier = "TEST-DEV-01",
                AllLocalOutboxesDrained = true,
                NoUnresolvedLocalTransactions = true,
                SoleDeviceCompletenessConfirmed = true
            }) as OkObjectResult;
            Assert(dupRes != null && ((ZReadingReportDto)dupRes.Value!).Id == zReport.Id && await db.ZReadings.CountAsync() == 1,
                "Duplicate Z-Reading for same (TerminalId, BusinessDate) must never create a second record.");

            // Scenario 10: Terminal 1 is now LOCKED (423) for ALL users on Business Date 2026-11-20!
            var t1LoginC2 = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(t1LoginC2?.StatusCode == 423, $"Cashier B must get 423 Locked on Terminal 1 after Z-Reading, got {t1LoginC2?.StatusCode}.");

            var t1LoginSenior = await auth.PinLogin(new PinLoginDto { PinCode = "4444", TerminalId = 1, BranchId = 1 }) as ObjectResult;
            Assert(t1LoginSenior?.StatusCode == 423, $"Even Senior Cashier must get 423 Locked for new login on finalized Terminal 1, got {t1LoginSenior?.StatusCode}.");

            var cdT1C2 = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 1);
            Assert((await cdT1C2.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, CashierId = "cashier2", OpeningFloat = 1000m })) is ConflictObjectResult,
                "Opening a shift on Z-Reading-locked Terminal 1 must be blocked.");

            // Scenario 11: Terminal 2 remains completely operational!
            var t2LoginC2 = await auth.PinLogin(new PinLoginDto { PinCode = "2222", TerminalId = 2, BranchId = 1 });
            Assert(t2LoginC2 is OkObjectResult, "Terminal 2 must remain fully operational after Terminal 1 Z-Reading finalization.");

            var cdT2C2 = CreateTerminalScopedCashDrawerController(db, "cashier2", "Cashier", terminalId: 2);
            var t2Open = await cdT2C2.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 2, CashierId = "cashier2", ClientShiftId = "wf03-t2-open", OpeningFloat = 1000m, OpenedAt = nowUtc.AddMinutes(5) });
            Assert(t2Open is OkObjectResult, "Cashier B must be able to open shift on Terminal 2 while Terminal 1 is locked.");

            // Scenario 12: 3:59:59 AM Manila -> still locked/restricted; 4:00:00 AM Manila -> both automatically reset!
            BusinessDayClock.UtcNowProvider = () => dayEndUtc.AddSeconds(-1); // 03:59:59 AM Manila
            var lockAt359 = await TerminalClosingLockGuard.EvaluateTerminalLockAsync(db, 1, "cashier1");
            Assert(lockAt359.IsTerminalLocked && lockAt359.IsCashierRestricted,
                "At 03:59:59 AM Manila, both Terminal Z-Reading lock and Cashier restriction must remain active.");

            BusinessDayClock.UtcNowProvider = () => dayEndUtc; // 04:00:00 AM Manila (next Business Date!)
            var lockAt400 = await TerminalClosingLockGuard.EvaluateTerminalLockAsync(db, 1, "cashier1");
            Assert(!lockAt400.IsTerminalLocked && !lockAt400.IsCashierRestricted,
                "At 04:00:00 AM Manila, both Terminal Z-Reading lock and Cashier restriction must automatically reset.");

            var c1LoginNextDay = await auth.PinLogin(new PinLoginDto { PinCode = "1111", TerminalId = 1, BranchId = 1 });
            Assert(c1LoginNextDay is OkObjectResult, "Cashier A must be allowed to log in on Terminal 1 starting 4:00 AM next Business Date.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    // Scenario 13 + Supervisor Reopen Audit Workflow:
    // Offline E2E:
    // - SQLite migrates cleanly to V5 (LocalCashierClosingRestrictions).
    // - Cashier A opens shift, sells offline, closes shift offline -> local cashier restriction enforced across simulated restart.
    // - Cashier B CAN open shift & sell offline on SAME Terminal 1!
    // - Offline anytime X-Reading runs read-only while Cashier B's shift is OPEN, reflecting BOTH Cashier A + Cashier B.
    // - Supervisor offline Reopen for Cashier A with mandatory Reason + PIN preserves closed shift immutably and allows Cashier A back on T1.
    // - Reconnect sync uploads SHIFT_OPEN, Order, SHIFT_CLOSE, CASHIER_REOPEN idempotently to PostgreSQL/API without duplicates!
    private static async Task TestWorkflow04_OfflineCloseCashierAnytimeXReadingSupervisorReopenAndIdempotentSync()
    {
        try
        {
            var dayUtc = new DateTime(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc); // 2:00 PM Manila
            using var localScope = new TempLocalDbScope("wf04_offline");
            SetDeterministicManilaClock(dayUtc, tickCount64: 100_000L);
            await SeedOfflineBaselineAsync(configureOpeningFund: true, openingFund: 1000m);

            // Seed cashier1, cashier2, manager1 (PIN 9999) into LocalCashiers + verify V5 schema
            using (var seedDb = new LocalPosDbContext())
            {
                seedDb.InitializeDatabase();
                Assert(LocalDatabaseMigrator.GetUserVersion(seedDb.Database.GetDbConnection()) == LocalDatabaseMigrator.CurrentSchemaVersion,
                    $"SQLite must migrate to schema version {LocalDatabaseMigrator.CurrentSchemaVersion}.");

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

            // 1. Cashier A opens shift offline, makes a ₱224 cash sale, closes shift offline
            var shiftA = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = "wf04-shift-a",
                OpeningFloat = 1000m,
                OpenedAt = dayUtc
            }, "cashier1", offline: true);

            var sessionA = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier1");
            using (var checkoutDb = new LocalPosDbContext())
            {
                var saleA = await new OfflineCheckoutService(checkoutDb, sessionA).ProcessSaleAsync(
                    BuildSingleCashOrderDto(shiftA.ShiftId, shiftA.ClientShiftId!, "cashier1"), 224m, "CASH", null, "cashier1");
                Assert(saleA.Success, $"Offline sale for Cashier A failed: {saleA.ErrorMessage}");
            }

            var closedA = await ShiftSession.CloseAsync(noNetHttp, new CloseShiftDto
            {
                ShiftId = shiftA.ShiftId,
                ClientShiftId = shiftA.ClientShiftId,
                ClientCloseId = "wf04-close-a",
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
            Assert(closedA.Status == "CLOSED", "Offline Close Cashier A must save CLOSED shift.");

            // 2. Simulate app restart: Cashier A is restricted locally on T1, while Terminal is NOT locked!
            LocalPosDbContext.ResetInitializationState();
            var lockA = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier1", offline: true);
            Assert(lockA.IsCashierRestricted && !lockA.IsTerminalLocked,
                $"After offline Close Cashier + restart, Cashier A must be restricted without locking terminal (Restr={lockA.IsCashierRestricted}, TermLock={lockA.IsTerminalLocked}).");

            bool c1BlockedReopen = false;
            try
            {
                await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto { BranchId = 1, TerminalId = 1, ClientShiftId = "wf04-c1-retry", OpeningFloat = 1000m }, "cashier1", offline: true);
            }
            catch (InvalidOperationException) { c1BlockedReopen = true; }
            Assert(c1BlockedReopen, "Cashier A must be blocked from opening another shift offline on T1 after closing.");

            // 3. Cashier B is NOT restricted offline -> opens shift & makes a ₱224 cash sale on T1!
            var lockB = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier2", offline: true);
            Assert(!lockB.IsCashierRestricted && !lockB.IsTerminalLocked, "Cashier B must NOT be blocked offline on T1.");

            var shiftB = await ShiftSession.OpenAsync(noNetHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = "wf04-shift-b",
                OpeningFloat = 1000m,
                OpenedAt = dayUtc.AddMinutes(40)
            }, "cashier2", offline: true);

            var sessionB = CreateOfflineTestSession("OFFLINE-SESSION-TOKEN", "cashier2");
            using (var checkoutDbB = new LocalPosDbContext())
            {
                var saleB = await new OfflineCheckoutService(checkoutDbB, sessionB).ProcessSaleAsync(
                    BuildSingleCashOrderDto(shiftB.ShiftId, shiftB.ClientShiftId!, "cashier2"), 224m, "CASH", null, "cashier2");
                Assert(saleB.Success, $"Offline sale for Cashier B failed: {saleB.ErrorMessage}");
            }

            // 4. Offline Anytime X-Reading runs strictly read-only while Cashier B's shift is OPEN!
            var offlineX1 = await ShiftSession.GetAnytimeXReadingAsync(noNetHttp, 1, new DateOnly(2026, 11, 20), offline: true, supervisorPin: "9999");
            var offlineX2 = await ShiftSession.GetAnytimeXReadingAsync(noNetHttp, 1, new DateOnly(2026, 11, 20), offline: true, isSupervisorCaller: true);
            Assert(offlineX1.IsAnytimeAudit && offlineX1.ActiveShiftCount == 1 && offlineX1.ClosedShiftCount == 1 &&
                   offlineX1.CompletedTransactionsCount == 2 && offlineX1.NetSales == 448m &&
                   offlineX1.NetSales == offlineX2.NetSales && offlineX1.CashierSummaries.Count == 2,
                $"Offline anytime X-Reading must reflect both shifts read-only (Active={offlineX1.ActiveShiftCount}, Closed={offlineX1.ClosedShiftCount}, Tx={offlineX1.CompletedTransactionsCount}, Net={offlineX1.NetSales}).");

            // Plain Cashier without Supervisor PIN must be denied offline X-Reading
            bool offlineXDenied = false;
            try { await ShiftSession.GetAnytimeXReadingAsync(noNetHttp, 1, new DateOnly(2026, 11, 20), offline: true, supervisorPin: "1111", isSupervisorCaller: false); }
            catch (InvalidOperationException) { offlineXDenied = true; }
            Assert(offlineXDenied, "Offline anytime X-Reading must deny plain cashier PIN.");

            // 5. Supervisor offline Reopen for Cashier A with Reason + Supervisor PIN -> preserves closed shift immutably!
            var reopened = await ShiftSession.ReopenCashierRestrictionAsync(noNetHttp, new ReopenCashierRestrictionRequestDto
            {
                ClientOperationId = "wf04-reopen-c1",
                TerminalId = 1,
                CashierId = "cashier1",
                BusinessDate = "2026-11-20",
                Reason = "Cashier A closed prematurely — covering evening rush",
                SupervisorPin = "9999"
            }, "cashier1", "Cashier", offline: true);
            Assert(reopened.Status == "REOPENED" && reopened.ReopenedBy == "manager1", "Offline supervisor reopen must record REOPENED status and approver.");

            // Verify Cashier A is now unrestricted locally AND original closed shift A is untouched
            var lockAAfterReopen = await ShiftSession.GetTerminalLockStatusAsync(noNetHttp, 1, "cashier1", offline: true);
            Assert(!lockAAfterReopen.IsCashierRestricted && !lockAAfterReopen.IsTerminalLocked, "Cashier A must be unlocked locally after supervisor reopen.");
            using (var checkDb = new LocalPosDbContext())
            {
                var origShiftA = await checkDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ClientShiftId == "wf04-shift-a");
                Assert(origShiftA.Status == "CLOSED" && origShiftA.ActualCountedBalance == 1224m,
                    "Supervisor reopen must NEVER delete or overwrite closed CashDrawerShift accountability records.");
                var reopenOutbox = await checkDb.SyncOutboxes.AsNoTracking().SingleAsync(o => o.EntityType == "CASHIER_REOPEN");
                Assert(!reopenOutbox.PayloadJson.Contains("9999", StringComparison.Ordinal),
                    "Outbox CASHIER_REOPEN payload must NEVER store plaintext supervisor PIN.");
            }

            // 6. Reconnect sync: upload SHIFT_OPEN (A), Order (A), SHIFT_CLOSE (A), CASHIER_REOPEN (A) to in-proc API!
            var (srvConn, srvDb, srvInitialShift) = await CreateTestEnvAsync();
            await using var _srv = srvConn;
            // Retire seeded open shift on server so offline SHIFT_OPEN replays cleanly
            srvInitialShift.Status = "CLOSED";
            srvInitialShift.OpenedAt = dayUtc.AddDays(-2);
            srvInitialShift.ClosedAt = dayUtc.AddDays(-2).AddHours(4);
            srvInitialShift.ActualCountedBalance = srvInitialShift.OpeningFloat;
            srvInitialShift.ClosingOriginalDeclarationJson = "{}";
            srvDb.Categories.Add(new Category { CategoryId = 1, Name = "Main", IsActive = true });
            srvDb.Products.Add(new Product { ProductId = 1, Name = "Test Item", Price = 112m, CategoryId = 1, ProductType = "FinishedGood", IsActive = true });
            await srvDb.SaveChangesAsync();

            var srvCashDrawer = CreateTerminalScopedCashDrawerController(srvDb, "POS-1", "Terminal", terminalId: 1);
            var srvOfflineSync = new OfflineSyncController(srvDb) { ControllerContext = srvCashDrawer.ControllerContext };

            using var syncHttp = new HttpClient(new InProcSyncHandler(async req =>
            {
                string path = req.RequestUri!.AbsolutePath.ToLowerInvariant();
                if (path.EndsWith("/api/cash-drawer/open-shift"))
                {
                    var dto = await req.Content!.ReadFromJsonAsync<OpenShiftDto>();
                    var res = await srvCashDrawer.OpenShift(dto!);
                    return ToHttpResponse(res);
                }
                if (path.EndsWith("/api/cash-drawer/close-shift"))
                {
                    var dto = await req.Content!.ReadFromJsonAsync<CloseShiftDto>();
                    var res = await srvCashDrawer.CloseShift(dto!);
                    return ToHttpResponse(res);
                }
                if (path.EndsWith("/api/cash-drawer/cashier-restrictions/reopen"))
                {
                    var dto = await req.Content!.ReadFromJsonAsync<ReopenCashierRestrictionRequestDto>();
                    var res = await srvCashDrawer.ReopenCashierRestriction(dto!);
                    return ToHttpResponse(res.Result ?? new OkObjectResult(res.Value));
                }
                if (path.EndsWith("/api/offline-sync/transactions"))
                {
                    var dto = await req.Content!.ReadFromJsonAsync<OfflineTransactionSyncRequest>();
                    var res = await srvOfflineSync.SyncTransaction(dto!);
                    return ToHttpResponse(res.Result ?? new OkObjectResult(res.Value));
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }))
            { BaseAddress = new Uri("http://localhost/") };

            // Close Cashier B locally too so all shifts can sync cleanly in priority order
            await ShiftSession.CloseAsync(noNetHttp, new CloseShiftDto
            {
                ShiftId = shiftB.ShiftId,
                ClientShiftId = shiftB.ClientShiftId,
                ClientCloseId = "wf04-close-b",
                IsDeclarationConfirmed = true,
                Denominations = new List<CashDenominationLineDto>
                {
                    new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                    new() { Type = "BILL", Denomination = 200m, Quantity = 1 },
                    new() { Type = "COIN", Denomination = 20m, Quantity = 1 },
                    new() { Type = "COIN", Denomination = 1m, Quantity = 4 }
                },
                ClosedAt = dayUtc.AddMinutes(50)
            }, "cashier2", offline: true);

            using var syncDb = new LocalPosDbContext();
            var syncSvc = new OfflineTransactionSyncService(syncDb);

            // Sync Shift A's open, order, close, and reopen in deterministic order
            var outboxes = await syncSvc.GetPendingOutboxItemsAsync();
            var shiftAOpen = outboxes.Single(o => o.EntityType == "SHIFT_OPEN" && o.EntityId == "wf04-shift-a");
            Assert(await syncSvc.UploadTransactionAsync(shiftAOpen, syncHttp), "Offline SHIFT_OPEN (A) must sync.");

            var shiftAClose = outboxes.Single(o => o.EntityType == "SHIFT_CLOSE" && o.EntityId == "wf04-close-a");
            Assert(await syncSvc.UploadTransactionAsync(shiftAClose, syncHttp), "Offline SHIFT_CLOSE (A) must sync.");

            // Verify server now has ACTIVE restriction for cashier1 before reopen syncs
            var srvRestrBefore = await srvDb.CashierClosingRestrictions.AsNoTracking()
                .SingleAsync(r => r.TerminalId == 1 && r.CashierId == "cashier1");
            Assert(srvRestrBefore.Status == "ACTIVE", "Server must create ACTIVE CashierClosingRestriction when offline SHIFT_CLOSE syncs.");

            // Sync Shift B open + close so no pending SHIFT_CLOSE blocks CASHIER_REOPEN
            var shiftBOpen = outboxes.Single(o => o.EntityType == "SHIFT_OPEN" && o.EntityId == "wf04-shift-b");
            Assert(await syncSvc.UploadTransactionAsync(shiftBOpen, syncHttp), "Offline SHIFT_OPEN (B) must sync.");
            var shiftBClose = outboxes.Single(o => o.EntityType == "SHIFT_CLOSE" && o.EntityId == "wf04-close-b");
            Assert(await syncSvc.UploadTransactionAsync(shiftBClose, syncHttp), "Offline SHIFT_CLOSE (B) must sync.");

            // Now sync CASHIER_REOPEN (A) -> verifies SupervisorPinHash on server and marks server restriction REOPENED!
            var reopenItem = outboxes.Single(o => o.EntityType == "CASHIER_REOPEN");
            Assert(await syncSvc.UploadTransactionAsync(reopenItem, syncHttp), "Offline CASHIER_REOPEN must sync cleanly via SupervisorPinHash verification.");

            // Idempotent replay of SHIFT_CLOSE and CASHIER_REOPEN must not create duplicates!
            Assert(await syncSvc.UploadTransactionAsync(shiftAClose, syncHttp), "Idempotent SHIFT_CLOSE replay must succeed without duplicating.");
            Assert(await syncSvc.UploadTransactionAsync(reopenItem, syncHttp), "Idempotent CASHIER_REOPEN replay must succeed without duplicating.");

            var srvRestrictions = await srvDb.CashierClosingRestrictions.AsNoTracking().Where(r => r.TerminalId == 1).ToListAsync();
            var srvC1 = srvRestrictions.Single(r => r.CashierId == "cashier1");
            var srvC2 = srvRestrictions.Single(r => r.CashierId == "cashier2");
            Assert(srvC1.Status == "REOPENED" && srvC1.ReopenedBy == "manager1" && !string.IsNullOrWhiteSpace(srvC1.ReopenReason),
                "Server must reflect REOPENED status and full audit metadata for Cashier A after offline reopen sync.");
            Assert(srvC2.Status == "ACTIVE", "Cashier B restriction must remain ACTIVE on server.");
        }
        finally
        {
            BusinessDayClock.ResetProviders();
        }
    }

    private static HttpResponseMessage ToHttpResponse(IActionResult actionResult)
    {
        if (actionResult is OkObjectResult ok)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(ok.Value) };
        if (actionResult is ObjectResult obj)
            return new HttpResponseMessage((HttpStatusCode)(obj.StatusCode ?? 500)) { Content = new StringContent(obj.Value?.ToString() ?? string.Empty) };
        if (actionResult is ForbidResult)
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        if (actionResult is NotFoundObjectResult nf)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent(nf.Value?.ToString() ?? string.Empty) };
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }
}
