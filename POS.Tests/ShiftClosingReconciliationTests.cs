using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task Test41_DenominationCountingAndCentavoArithmetic()
    {
        // 1. Validate catalog defaults: 8 coins (0.01..20) and 6 bills (20..1000)
        var defaults = CashDenominationCatalog.CreateDefaultLines();
        Assert(defaults.Count == 14, $"Expected 14 denomination lines, got {defaults.Count}");
        Assert(defaults.Count(d => d.Type == "COIN") == 8, "Expected 8 coin denominations");
        Assert(defaults.Count(d => d.Type == "BILL") == 6, "Expected 6 bill denominations");
        Assert(defaults.Any(d => d.Type == "COIN" && d.Denomination == 20m), "Expected ₱20 coin");
        Assert(defaults.Any(d => d.Type == "BILL" && d.Denomination == 20m), "Expected ₱20 bill");

        // 2. Zero counts validate cleanly to 0.00
        Assert(CashDenominationCatalog.TryValidateAndCalculate(defaults, out _, out decimal zeroTotal, out _), "Default zero lines must validate");
        Assert(zeroTotal == 0m, $"Expected 0.00 total for zero quantities, got {zeroTotal}");

        // 3. Centavo & mixed coin/bill arithmetic (including separate ₱20 coin and ₱20 bill)
        var custom = new List<CashDenominationLineDto>
        {
            new() { Type = "COIN", Denomination = 0.01m, Quantity = 3 },  // 0.03
            new() { Type = "COIN", Denomination = 0.05m, Quantity = 2 },  // 0.10
            new() { Type = "COIN", Denomination = 0.10m, Quantity = 4 },  // 0.40
            new() { Type = "COIN", Denomination = 0.25m, Quantity = 3 },  // 0.75 -> centavos = 1.28
            new() { Type = "COIN", Denomination = 1.00m, Quantity = 5 },  // 5.00
            new() { Type = "COIN", Denomination = 5.00m, Quantity = 2 },  // 10.00
            new() { Type = "COIN", Denomination = 10.00m, Quantity = 3 }, // 30.00
            new() { Type = "COIN", Denomination = 20.00m, Quantity = 4 }, // 80.00 -> coins = 126.28
            new() { Type = "BILL", Denomination = 20.00m, Quantity = 5 }, // 100.00
            new() { Type = "BILL", Denomination = 50.00m, Quantity = 2 }, // 100.00
            new() { Type = "BILL", Denomination = 100.00m, Quantity = 3 },// 300.00
            new() { Type = "BILL", Denomination = 200.00m, Quantity = 1 },// 200.00
            new() { Type = "BILL", Denomination = 500.00m, Quantity = 2 },// 1000.00
            new() { Type = "BILL", Denomination = 1000.00m, Quantity = 1, Subtotal = 999999m } // tampered client subtotal ignored
        };
        Assert(CashDenominationCatalog.TryValidateAndCalculate(custom, out var norm, out decimal counted, out _), "Valid centavo/bill breakdown must succeed");
        Assert(counted == 2826.28m, $"Expected 2826.28 total counted cash, got {counted}");
        Assert(norm.Single(d => d.Type == "COIN" && d.Denomination == 20m).Subtotal == 80.00m, "₱20 coin subtotal must be 80.00");
        Assert(norm.Single(d => d.Type == "BILL" && d.Denomination == 20m).Subtotal == 100.00m, "₱20 bill subtotal must be 100.00");
        Assert(norm.Single(d => d.Type == "BILL" && d.Denomination == 1000m).Subtotal == 1000.00m, "Server must recalculate tampered subtotal");

        // 4. Reject negative quantity, invalid denomination, and duplicates
        Assert(!CashDenominationCatalog.TryValidateAndCalculate([new() { Type = "COIN", Denomination = 1m, Quantity = -1 }], out _, out _, out _),
            "Negative denomination quantity must be rejected");
        Assert(!CashDenominationCatalog.TryValidateAndCalculate([new() { Type = "BILL", Denomination = 0.25m, Quantity = 1 }], out _, out _, out _),
            "Coin denomination submitted as BILL must be rejected");
        Assert(!CashDenominationCatalog.TryValidateAndCalculate(
            [new() { Type = "COIN", Denomination = 5m, Quantity = 1 }, new() { Type = "COIN", Denomination = 5m, Quantity = 2 }], out _, out _, out _),
            "Duplicate denomination lines must be rejected");

        // 5. Server CloseShift authoritatively recalculates ActualCountedBalance from Denominations
        //    and returns a blind-redacted DTO to the cashier while persisting full reconciliation for supervisors
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var closeRes = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            IsDeclarationConfirmed = true,
            ActualCountedBalance = 0m, // Server must override with 2826.28m from denominations
            Denominations = custom,
            Notes = "Centavo count submitted"
        });
        var ok = closeRes as OkObjectResult;
        Assert(ok?.Value is CashDrawerShiftDto, "Expected OkObjectResult with CashDrawerShiftDto");
        var closedDto = (CashDrawerShiftDto)ok!.Value!;
        Assert(closedDto.ActualCountedBalance == 2826.28m, $"Expected server ActualCountedBalance 2826.28, got {closedDto.ActualCountedBalance}");
        Assert(closedDto.IsBlindCloseRedacted, "Cashier CloseShift response must be blind-redacted");
        Assert(closedDto.ExpectedBalance == 0m && closedDto.Discrepancy == null && closedDto.XReading == null && closedDto.PaymentReconciliations.Count == 0,
            "Cashier CloseShift response must hide ExpectedBalance, Discrepancy, PaymentReconciliations, and XReading");
        Assert(closedDto.CashierDeclaration != null && closedDto.CashierDeclaration.TotalCountedCash == 2826.28m,
            "Cashier CloseShift response must include CashierDeclaration acknowledgment");

        var supController = CreateController(db, "manager1", "Manager");
        var supRes = (await supController.GetLastClosedShift(1)).Result as OkObjectResult;
        var supDto = (CashDrawerShiftDto)supRes!.Value!;
        Assert(!supDto.IsBlindCloseRedacted && supDto.ExpectedBalance == 2000m && supDto.Discrepancy == 826.28m,
            $"Supervisor must see ExpectedBalance 2000.00 and Discrepancy +826.28, got {supDto.ExpectedBalance} / {supDto.Discrepancy}");
    }

    private static async Task Test42_PaymentMethodReconciliationAndOffsettingVariances()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;

        db.PaymentMethods.AddRange(
            new PaymentMethod { PaymentMethodId = 1, Code = "CASH", Name = "Cash", IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { PaymentMethodId = 2, Code = "GCASH", Name = "GCash", IsActive = true, DisplayOrder = 2 },
            new PaymentMethod { PaymentMethodId = 3, Code = "MAYA", Name = "Maya", IsActive = true, DisplayOrder = 3 },
            new PaymentMethod { PaymentMethodId = 4, Code = "GRABPAY", Name = "GrabPay", IsActive = false, DisplayOrder = 4 }, // deactivated after use
            new PaymentMethod { PaymentMethodId = 5, Code = "UNUSED_INACTIVE", Name = "OldWallet", IsActive = false, DisplayOrder = 5 }
        );

        // Record shift orders & payments:
        // Order 1: GCASH 500.00
        // Order 2: MAYA 300.00
        // Order 3: GRABPAY (inactive method used in shift) 250.00
        db.Orders.AddRange(
            new Order { OrderId = 501, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, OrderNumber = "O-501", OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 500m, TotalAmount = 500m, CreatedAt = DateTime.UtcNow },
            new Order { OrderId = 502, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, OrderNumber = "O-502", OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 300m, TotalAmount = 300m, CreatedAt = DateTime.UtcNow },
            new Order { OrderId = 503, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, OrderNumber = "O-503", OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 250m, TotalAmount = 250m, CreatedAt = DateTime.UtcNow }
        );
        db.Payments.AddRange(
            new Payment { PaymentId = 501, OrderId = 501, PaymentMethod = "GCASH", Amount = 500m, AmountTendered = 500m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow },
            new Payment { PaymentId = 502, OrderId = 502, PaymentMethod = "MAYA", Amount = 300m, AmountTendered = 300m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow },
            new Payment { PaymentId = 503, OrderId = 503, PaymentMethod = "GRABPAY", Amount = 250m, AmountTendered = 250m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow }
        );
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(db, "cashier1", "Cashier");

        // Offsetting variance test:
        // Cash Expected = 2000.00 -> Counted = 2100.00 (+100.00 OVER)
        // GCash Expected = 500.00 -> Declared = 400.00 (-100.00 SHORT)
        // Maya Expected = 300.00 -> Declared = 300.00 (BALANCED)
        // GrabPay Expected = 250.00 -> Declared = 260.00 (+10.00 OVER)
        var closeAttempt = new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            IsDeclarationConfirmed = false, // Unconfirmed submission must be rejected
            Denominations = [new() { Type = "BILL", Denomination = 1000m, Quantity = 2 }, new() { Type = "BILL", Denomination = 100m, Quantity = 1 }],
            PaymentDeclarations =
            [
                new() { PaymentMethodCode = "GCASH", DeclaredAmount = 400.00m },
                new() { PaymentMethodCode = "MAYA", DeclaredAmount = 300.00m },
                new() { PaymentMethodCode = "GRABPAY", DeclaredAmount = 260.00m }
            ],
            Notes = "" // Blind closing does NOT require discrepancy remarks from cashiers!
        };

        var unconfirmedRes = await controller.CloseShift(closeAttempt);
        Assert(unconfirmedRes is BadRequestObjectResult, "Unconfirmed blind closing declaration must be rejected with 400 BadRequest");

        closeAttempt.IsDeclarationConfirmed = true;
        var okRes = await controller.CloseShift(closeAttempt) as OkObjectResult;
        Assert(okRes?.Value is CashDrawerShiftDto, "Confirmed blind close without discrepancy remarks must succeed for cashier");

        var cashierClosed = (CashDrawerShiftDto)okRes!.Value!;
        Assert(cashierClosed.IsBlindCloseRedacted && cashierClosed.PaymentReconciliations.Count == 0 && cashierClosed.XReading == null,
            "Cashier response must not leak payment reconciliations or X-Reading");

        var supController = CreateController(db, "manager1", "Manager");
        var supOk = (await supController.GetLastClosedShift(1)).Result as OkObjectResult;
        var closed = (CashDrawerShiftDto)supOk!.Value!;
        var recons = closed.PaymentReconciliations;

        // Unused inactive method must NOT appear, but GrabPay (inactive used in shift) MUST appear
        Assert(!recons.Any(r => r.PaymentMethodCode == "UNUSED_INACTIVE"), "Unused inactive payment method must not appear");
        var grab = recons.Single(r => r.PaymentMethodCode == "GRABPAY");
        Assert(!grab.IsActive && grab.ExpectedAmount == 250m && grab.DeclaredAmount == 260m && grab.Variance == 10m && grab.Status == "OVER",
            "Deactivated payment method used in shift must appear with accurate variance and OVER status");

        var cash = recons.Single(r => r.IsCash);
        Assert(cash.ExpectedAmount == 2000m && cash.DeclaredAmount == 2100m && cash.Variance == 100m && cash.Status == "OVER", "Cash row must be +100 OVER");

        var gcash = recons.Single(r => r.PaymentMethodCode == "GCASH");
        Assert(gcash.ExpectedAmount == 500m && gcash.DeclaredAmount == 400m && gcash.Variance == -100m && gcash.Status == "SHORT", "GCash row must be -100 SHORT");

        var maya = recons.Single(r => r.PaymentMethodCode == "MAYA");
        Assert(maya.ExpectedAmount == 300m && maya.DeclaredAmount == 300m && maya.Variance == 0m && maya.Status == "BALANCED", "Maya row must be BALANCED");

        Assert(closed.XReading != null && !closed.XReading.IsOverallBalanced && closed.XReading.OverallStatus == "MIXED DISCREPANCY",
            "Overall reconciliation must flag MIXED DISCREPANCY and never hide offsetting variances across payment methods");
    }

    private static async Task Test43_CashExpectedLedgerAndSalesSeparation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;

        // OpeningFloat = 2000.00
        // Gross Cash Sales Collected = 1500.00
        // Cash Refunds = 150.00 (Net Cash Sales = 1350.00)
        // Cash In = 300.00
        // Cash Out = 200.00
        // Cash Drops = 500.00
        // Expected Drawer Cash = 2000 + 1500 - 150 + 300 - 200 - 500 = 2950.00
        shift.TotalCashSales = 1500.00m;
        shift.TotalCashRefunds = 150.00m;
        shift.TotalCashIn = 300.00m;
        shift.TotalCashOut = 200.00m;
        shift.TotalCashDrops = 500.00m;

        db.Orders.Add(new Order
        {
            OrderId = 601, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId,
            OrderNumber = "O-601", InvoiceNumber = "INV-000601", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 1500m, TotalAmount = 1500m,
            VatableSales = 1339.29m, VatAmount = 160.71m, CreatedAt = DateTime.UtcNow
        });
        db.Payments.Add(new Payment
        {
            PaymentId = 601, OrderId = 601, PaymentMethod = "CASH", Amount = 1500m,
            AmountTendered = 2000m, ChangeAmount = 500m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow
        });
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var controller = CreateController(db, "cashier1", "Cashier");
        var res = await controller.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            IsDeclarationConfirmed = true,
            Denominations =
            [
                new() { Type = "BILL", Denomination = 1000m, Quantity = 2 },
                new() { Type = "BILL", Denomination = 500m, Quantity = 1 },
                new() { Type = "BILL", Denomination = 200m, Quantity = 2 },
                new() { Type = "BILL", Denomination = 50m, Quantity = 1 }
            ]
        }) as OkObjectResult;

        Assert(res?.Value is CashDrawerShiftDto, "Expected balanced close to succeed without remarks");
        var cashierDto = (CashDrawerShiftDto)res!.Value!;
        Assert(cashierDto.IsBlindCloseRedacted && cashierDto.ActualCountedBalance == 2950.00m, "Cashier receives blind-redacted DTO with counted cash");

        var supController = CreateController(db, "manager1", "Manager");
        var supOk = (await supController.GetLastClosedShift(1)).Result as OkObjectResult;
        var dto = (CashDrawerShiftDto)supOk!.Value!;
        Assert(dto.ExpectedBalance == 2950.00m, $"Expected drawer balance 2950.00 (no double-counting change or refunds), got {dto.ExpectedBalance}");
        Assert(dto.ActualCountedBalance == 2950.00m && dto.Discrepancy == 0m, "Expected zero variance");
        Assert(dto.XReading!.ShiftNetSales == 1350.00m, $"ShiftNetSales must be 1350.00 (excluding OpeningFloat and CashIn/Out/Drops), got {dto.XReading.ShiftNetSales}");
        Assert(dto.XReading.OpeningFloat == 2000.00m && dto.XReading.ExpectedCashInDrawer == 2950.00m, "X-Reading must separate OpeningFloat/Drawer Ledger from Sales");
    }

    private static async Task Test44_CloseIdempotencyAndXReadingReprint()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;
        var controller = CreateController(db, "cashier1", "Cashier");

        var closeReq = new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            ClientCloseId = "close-idem-001",
            IsDeclarationConfirmed = true,
            Denominations = [new() { Type = "BILL", Denomination = 1000m, Quantity = 2 }],
            PaymentDeclarations = []
        };

        var firstRes = await controller.CloseShift(closeReq) as OkObjectResult;
        var secondRes = await controller.CloseShift(closeReq) as OkObjectResult;
        Assert(firstRes != null && secondRes != null, "Duplicate CloseShift submission with same ClientCloseId must return 200 OK idempotently");

        // Cashier can fetch and reprint CashierDeclaration acknowledgment, which must NOT contain expected/variance/status
        var declRes = await controller.GetShiftDeclaration(shift.ShiftId, reprint: true);
        var declOk = declRes.Result as OkObjectResult;
        Assert(declOk?.Value is CashierDeclarationReportDto, "GetShiftDeclaration must return CashierDeclarationReportDto");
        var declReport = (CashierDeclarationReportDto)declOk!.Value!;
        Assert(declReport.IsReprint, "Declaration reprint flag must be true when requested as reprint");
        string declHtml = CashierDeclarationHtmlFormatter.Format(declReport);
        Assert(declHtml.Contains("CASHIER DECLARATION ACKNOWLEDGMENT", StringComparison.Ordinal), "Declaration HTML must contain acknowledgment header");
        Assert(declHtml.Contains("** REPRINT **", StringComparison.Ordinal), "Declaration HTML must contain REPRINT badge");
        Assert(declHtml.Contains("Cash Denomination Count", StringComparison.Ordinal), "Declaration HTML must contain Cash Denomination Count");
        Assert(declHtml.Contains("Declared Non-Cash Payments", StringComparison.Ordinal), "Declaration HTML must contain Declared Non-Cash Payments");
        Assert(!declHtml.Contains("Expected", StringComparison.OrdinalIgnoreCase) && !declHtml.Contains("Variance", StringComparison.OrdinalIgnoreCase) && !declHtml.Contains("X-READING", StringComparison.OrdinalIgnoreCase),
            "Cashier Declaration Acknowledgment HTML must never contain Expected, Variance, or X-Reading details");

        // Cashier without supervisor PIN is forbidden from GetShiftXReading
        var forbiddenXRes = await controller.GetShiftXReading(shift.ShiftId, reprint: true);
        Assert(forbiddenXRes.Result is ForbidResult, "Cashier without supervisor PIN must be forbidden from GetShiftXReading");

        // Supervisor can fetch and reprint X-Reading
        var supController = CreateController(db, "manager1", "Manager");
        var xRes = await supController.GetShiftXReading(shift.ShiftId, reprint: true);
        var xOk = xRes.Result as OkObjectResult;
        Assert(xOk?.Value is XReadingReportDto, "Supervisor GetShiftXReading must return XReadingReportDto");
        var report = (XReadingReportDto)xOk!.Value!;
        Assert(report.IsReprint, "Reprint flag must be true when requested as reprint");

        string html = XReadingHtmlFormatter.Format(report);
        Assert(html.Contains("X-READING &amp; SHIFT RECONCILIATION", StringComparison.Ordinal), "HTML must contain X-Reading header");
        Assert(html.Contains("** REPRINT **", StringComparison.Ordinal), "HTML must contain REPRINT badge");
        Assert(html.Contains("Cash Drawer Ledger", StringComparison.Ordinal), "HTML must contain Cash Drawer Ledger");
        Assert(html.Contains("Cash Denomination Count", StringComparison.Ordinal), "HTML must contain Cash Denomination Count");
        Assert(html.Contains("Payment Method Reconciliation", StringComparison.Ordinal), "HTML must contain Payment Method Reconciliation");
        Assert(html.Contains("COUNTERS NOT RESET", StringComparison.Ordinal), "HTML must state counters are not reset (non-Z-reading)");
    }

    private static async Task Test45_BlindClosingRoleGuardsAndZeroDeclarationConfirmation()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;

        db.PaymentMethods.Add(new PaymentMethod { PaymentMethodId = 10, Code = "GCASH", Name = "GCash", IsActive = true, DisplayOrder = 1 });
        shift.TotalCashSales = 850.00m;
        shift.ExpectedBalance = 2850.00m;
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var cashierController = CreateController(db, "cashier1", "Cashier");

        // 1. GetCurrentShift for cashier redacts expected cash and sales while providing ConfiguredNonCashMethods
        var currentRes = (await cashierController.GetCurrentShift(1)).Result as OkObjectResult;
        var currentDto = (CashDrawerShiftDto)currentRes!.Value!;
        Assert(currentDto.IsBlindCloseRedacted, "GetCurrentShift for Cashier must be blind-redacted");
        Assert(currentDto.ExpectedBalance == 0m && currentDto.TotalCashSales == 0m && currentDto.PaymentReconciliations.Count == 0,
            "GetCurrentShift for Cashier must hide ExpectedBalance, TotalCashSales, and PaymentReconciliations");
        Assert(currentDto.ConfiguredNonCashMethods.Any(m => m.PaymentMethodCode == "GCASH"),
            "GetCurrentShift for Cashier must include ConfiguredNonCashMethods");

        // 2. Direct endpoint calls by Cashier to reconciliation/summary endpoints are rejected with 403 Forbid
        Assert((await cashierController.GetShifts(1, null, null, null)).Result is ForbidResult, "GetShifts must forbid Cashier");
        Assert((await cashierController.GetShiftTotals(shift.ShiftId)).Result is ForbidResult, "GetShiftTotals must forbid Cashier");
        Assert((await cashierController.GetMovementSummaryReport(1, null, null, null)).Result is ForbidResult, "GetMovementSummaryReport must forbid Cashier");
        Assert((await cashierController.GetShiftXReading(shift.ShiftId)).Result is ForbidResult, "GetShiftXReading without supervisor PIN must forbid Cashier");

        // 3. Legitimate all-zero declaration requires explicit IsDeclarationConfirmed = true
        var zeroDeclReq = new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            IsDeclarationConfirmed = false,
            Denominations = CashDenominationCatalog.CreateDefaultLines(),
            PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 0m }]
        };
        Assert(await cashierController.CloseShift(zeroDeclReq) is BadRequestObjectResult,
            "All-zero declaration without IsDeclarationConfirmed=true must be rejected");

        zeroDeclReq.IsDeclarationConfirmed = true;
        var zeroCloseOk = await cashierController.CloseShift(zeroDeclReq) as OkObjectResult;
        Assert(zeroCloseOk?.Value is CashDrawerShiftDto, "Explicitly confirmed all-zero declaration must succeed");
        var zeroClosedDto = (CashDrawerShiftDto)zeroCloseOk!.Value!;
        Assert(zeroClosedDto.ActualCountedBalance == 0m && zeroClosedDto.TotalNonCashDeclared == 0m && zeroClosedDto.Discrepancy == null,
            "Confirmed all-zero declaration must record 0.00 counted/declared and hide variance from cashier");
    }

    private static async Task Test46_SupervisorRecountPreservesOriginalDeclarationAndAuditTrail()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;

        db.PaymentMethods.Add(new PaymentMethod { PaymentMethodId = 10, Code = "GCASH", Name = "GCash", IsActive = true, DisplayOrder = 1 });
        db.Orders.Add(new Order { OrderId = 701, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId, OrderNumber = "O-701", OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID", Subtotal = 250m, TotalAmount = 250m, CreatedAt = DateTime.UtcNow });
        db.Payments.Add(new Payment { PaymentId = 701, OrderId = 701, PaymentMethod = "GCASH", Amount = 250m, AmountTendered = 250m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow });
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var cashierController = CreateController(db, "cashier1", "Cashier");

        // Cashier submits blind declaration with 1500.00 cash (Expected 2000.00 -> -500.00 short) and 200.00 GCash (Expected 250.00 -> -50.00 short)
        var closeOk = await cashierController.CloseShift(new CloseShiftDto
        {
            ShiftId = shift.ShiftId,
            IsDeclarationConfirmed = true,
            Denominations = [new() { Type = "BILL", Denomination = 1000m, Quantity = 1 }, new() { Type = "BILL", Denomination = 500m, Quantity = 1 }],
            PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 200.00m }],
            Notes = "Initial blind count"
        }) as OkObjectResult;
        Assert(closeOk != null, "Initial cashier blind close must succeed");

        // 1. Unauthorized recount by cashier without supervisor PIN must be forbidden
        var unauthorizedRecount = await cashierController.RecountShift(shift.ShiftId, new ShiftRecountRequestDto
        {
            ShiftId = shift.ShiftId,
            Reason = "Cashier trying to edit own closed shift",
            Denominations = [new() { Type = "BILL", Denomination = 1000m, Quantity = 2 }]
        });
        Assert(unauthorizedRecount.Result is ForbidResult, "Cashier without supervisor PIN must be forbidden from recounting a closed shift");

        // 2. Supervisor recount without reason must be rejected
        var supController = CreateController(db, "manager1", "Manager");
        var missingReasonRecount = await supController.RecountShift(shift.ShiftId, new ShiftRecountRequestDto
        {
            ShiftId = shift.ShiftId,
            Reason = "",
            Denominations = [new() { Type = "BILL", Denomination = 1000m, Quantity = 2 }]
        });
        Assert(missingReasonRecount.Result is BadRequestObjectResult, "Supervisor recount without a reason must be rejected");

        // 3. Authorized supervisor recount updates reconciliation while preserving OriginalDeclaration and appending RecountHistory
        var recountRes = await supController.RecountShift(shift.ShiftId, new ShiftRecountRequestDto
        {
            ShiftId = shift.ShiftId,
            Reason = "Supervisor physical recount found ₱450 bill envelope and missing ₱50 GCash slip",
            Denominations =
            [
                new() { Type = "BILL", Denomination = 1000m, Quantity = 1 },
                new() { Type = "BILL", Denomination = 500m, Quantity = 1 },
                new() { Type = "BILL", Denomination = 200m, Quantity = 2 },
                new() { Type = "BILL", Denomination = 50m, Quantity = 1 }
            ],
            PaymentDeclarations = [new() { PaymentMethodCode = "GCASH", DeclaredAmount = 250.00m }]
        });

        var recountOk = recountRes.Result as OkObjectResult;
        Assert(recountOk?.Value is CashDrawerShiftDto, "Authorized supervisor recount must return OkObjectResult with CashDrawerShiftDto");
        var recounted = (CashDrawerShiftDto)recountOk!.Value!;

        Assert(recounted.ActualCountedBalance == 1950.00m && recounted.Discrepancy == -50.00m,
            $"Expected revised counted cash 1950.00 and discrepancy -50.00, got {recounted.ActualCountedBalance} / {recounted.Discrepancy}");
        Assert(recounted.TotalNonCashDeclared == 250.00m, $"Expected revised non-cash declared 250.00, got {recounted.TotalNonCashDeclared}");
        Assert(recounted.OriginalDeclaration != null && recounted.OriginalDeclaration.TotalCountedCash == 1500.00m && recounted.OriginalDeclaration.TotalNonCashDeclared == 200.00m,
            "Original cashier declaration (1500.00 cash, 200.00 non-cash) must be preserved unchanged");
        Assert(recounted.RecountHistory.Count == 1, "RecountHistory must contain 1 audit entry");

        var audit = recounted.RecountHistory[0];
        Assert(audit.RecountNumber == 1 && audit.CorrectedBy == "manager1", "Audit entry must record RecountNumber=1 and CorrectedBy=manager1");
        Assert(audit.OriginalCountedCash == 1500.00m && audit.RevisedCountedCash == 1950.00m, "Audit entry must record original and revised counted cash");
        Assert(audit.OriginalCashVariance == -500.00m && audit.RevisedCashVariance == -50.00m, "Audit entry must record original and revised cash variance");
        Assert(audit.Reason.Contains("Supervisor physical recount", StringComparison.Ordinal), "Audit entry must record recount reason");

        string xHtml = XReadingHtmlFormatter.Format(recounted.XReading!);
        Assert(xHtml.Contains("Supervisor Recount / Correction Audit", StringComparison.Ordinal), "X-Reading HTML must include Supervisor Recount / Correction Audit section");
    }

    private static async Task Test47_SalesReportTerminalBreakdownAndSeparationOfRefunds()
    {
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var connScope = conn;

        // Add Outlet 2 and Terminal 2
        db.Outlets.Add(new Outlet { OutletId = 2, BranchId = 1, OutletCode = "KIOSK", Name = "Kiosk Booth", IsActive = true });
        db.Terminals.Add(new Terminal { TerminalId = 2, BranchId = 1, OutletId = 1, TerminalCode = "POS-2", Name = "Counter 2", IsActive = true });

        db.PaymentMethods.AddRange(
            new PaymentMethod { PaymentMethodId = 1, Code = "CASH", Name = "Cash", IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { PaymentMethodId = 2, Code = "GCASH", Name = "GCash", IsActive = true, DisplayOrder = 2 },
            new PaymentMethod { PaymentMethodId = 3, Code = "MAYA", Name = "Maya", IsActive = true, DisplayOrder = 3 }
        );

        // Order 1: Terminal 1, Subtotal 1000, Total 1000, CASH 1000, PAID
        db.Orders.Add(new Order
        {
            OrderId = 801, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = 1, OrderNumber = "ORD-801",
            OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID",
            Subtotal = 1000m, DiscountAmount = 0m, TotalAmount = 1000m, VatableSales = 892.86m, VatAmount = 107.14m, CreatedAt = DateTime.UtcNow
        });
        var pay801 = new Payment
        {
            PaymentId = 801, OrderId = 801, PaymentMethod = "CASH", Amount = 1000m, AmountTendered = 1000m, ChangeAmount = 0m,
            PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow
        };
        db.Payments.Add(pay801);

        // Order 2: Terminal 2, Subtotal 600, Discount 100, Total 500, Split Payment (CASH 200, GCASH 300), PAID
        db.Orders.Add(new Order
        {
            OrderId = 802, BranchId = 1, OutletId = 1, TerminalId = 2, ShiftId = 1, OrderNumber = "ORD-802",
            OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID",
            Subtotal = 600m, DiscountAmount = 100m, TotalAmount = 500m, VatableSales = 446.43m, VatAmount = 53.57m, CreatedAt = DateTime.UtcNow
        });
        db.Payments.AddRange(
            new Payment { PaymentId = 802, OrderId = 802, PaymentMethod = "CASH", Amount = 200m, AmountTendered = 200m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow },
            new Payment { PaymentId = 803, OrderId = 802, PaymentMethod = "GCASH", Amount = 300m, AmountTendered = 300m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow }
        );

        // Order 3: Terminal 1, Total 400, VOIDED
        db.Orders.Add(new Order
        {
            OrderId = 803, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = 1, OrderNumber = "ORD-803",
            OrderType = "DINE_IN", CashierId = "cashier1", OrderStatus = "VOIDED", PaymentStatus = "VOIDED",
            Subtotal = 400m, TotalAmount = 400m, CreatedAt = DateTime.UtcNow
        });

        // Order 4: Legacy unmatched Terminal (TerminalId = 9999), Outlet 2, Total 300, MAYA 300, PAID
        db.Orders.Add(new Order
        {
            OrderId = 804, BranchId = 1, OutletId = 2, TerminalId = 9999, ShiftId = 1, OrderNumber = "ORD-804",
            OrderType = "TAKE_OUT", CashierId = "cashier1", OrderStatus = "COMPLETED", PaymentStatus = "PAID",
            Subtotal = 300m, TotalAmount = 300m, CreatedAt = DateTime.UtcNow
        });
        db.Payments.Add(new Payment
        {
            PaymentId = 804, OrderId = 804, PaymentMethod = "MAYA", Amount = 300m, AmountTendered = 300m, ChangeAmount = 0m,
            PaymentStatus = "SUCCESS", ProcessedBy = "cashier1", PaidAt = DateTime.UtcNow
        });

        // Partial Refund of 150.00 CASH on Order 801
        db.SalesReturns.Add(new SalesReturn
        {
            ReturnId = 801, OriginalOrderId = 801, BranchId = 1, TerminalId = 1, ReturnReference = "RET-801",
            ReturnReason = "Customer returned damaged item", Status = "COMPLETED", RequestedBy = "cashier1", TotalRefundAmount = 150m
        });
        db.Refunds.Add(new Refund
        {
            RefundId = 801, ReturnId = 801, OriginalPaymentId = 801, RefundMethod = "CASH", RefundAmount = 150m,
            Status = "COMPLETED", OriginalPayment = pay801
        });

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var reportService = new SalesReportService(db);

        // 1. Full Branch Report
        var fullReport = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1, Grouping = "TERMINAL" });

        Assert(fullReport.CompletedTransactionsCount == 3, $"Expected 3 paid orders, got {fullReport.CompletedTransactionsCount}");
        Assert(fullReport.VoidedTransactionsCount == 1, $"Expected 1 voided order, got {fullReport.VoidedTransactionsCount}");
        Assert(fullReport.VoidedAmount == 400.00m, $"Expected voided amount 400.00, got {fullReport.VoidedAmount}");
        Assert(fullReport.ReturnTransactionsCount == 1, $"Expected 1 return transaction, got {fullReport.ReturnTransactionsCount}");

        // Sales totals: Gross = 1000 + 600 + 300 = 1900; Discounts = 100; Net = 1800; Refunds = 150; Net After Refunds = 1650
        Assert(fullReport.GrossSales == 1900.00m, $"Expected GrossSales 1900.00, got {fullReport.GrossSales}");
        Assert(fullReport.TotalDiscounts == 100.00m, $"Expected TotalDiscounts 100.00, got {fullReport.TotalDiscounts}");
        Assert(fullReport.NetSales == 1800.00m, $"Expected NetSales 1800.00, got {fullReport.NetSales}");
        Assert(fullReport.TotalRefunds == 150.00m, $"Expected TotalRefunds 150.00, got {fullReport.TotalRefunds}");
        Assert(fullReport.NetSalesAfterRefunds == 1650.00m, $"Expected NetSalesAfterRefunds 1650.00, got {fullReport.NetSalesAfterRefunds}");

        // Payments collected: Cash collected = 1200 - 150 = 1050; NonCash collected = 600
        Assert(fullReport.TotalPaymentsCollected == 1800.00m, $"Expected TotalPaymentsCollected 1800.00, got {fullReport.TotalPaymentsCollected}");
        Assert(fullReport.NetPaymentsAfterRefunds == 1650.00m, $"Expected NetPaymentsAfterRefunds 1650.00, got {fullReport.NetPaymentsAfterRefunds}");
        Assert(fullReport.CashSalesCollected == 1050.00m, $"Expected CashSalesCollected 1050.00, got {fullReport.CashSalesCollected}");
        Assert(fullReport.NonCashSalesCollected == 600.00m, $"Expected NonCashSalesCollected 600.00, got {fullReport.NonCashSalesCollected}");

        // 2. Payment Breakdown: verify split payments did not duplicate order rows
        Assert(fullReport.PaymentBreakdown.Count == 3, $"Expected 3 payment methods in breakdown, got {fullReport.PaymentBreakdown.Count}");
        var cashBreakdown = fullReport.PaymentBreakdown.Single(p => p.PaymentMethodCode == "CASH");
        Assert(cashBreakdown.PaymentCount == 2 && cashBreakdown.GrossCollected == 1200.00m && cashBreakdown.RefundedAmount == 150.00m && cashBreakdown.NetCollected == 1050.00m,
            "CASH breakdown must reflect 2 payments totaling 1200.00 gross and 150.00 refund");
        var gcashBreakdown = fullReport.PaymentBreakdown.Single(p => p.PaymentMethodCode == "GCASH");
        Assert(gcashBreakdown.PaymentCount == 1 && gcashBreakdown.GrossCollected == 300.00m && gcashBreakdown.RefundedAmount == 0m,
            "GCASH breakdown must reflect 1 payment of 300.00");
        var mayaBreakdown = fullReport.PaymentBreakdown.Single(p => p.PaymentMethodCode == "MAYA");
        Assert(mayaBreakdown.PaymentCount == 1 && mayaBreakdown.GrossCollected == 300.00m,
            "MAYA breakdown must reflect 1 payment of 300.00");

        // 3. Terminal Breakdown: verify Terminal 1, Terminal 2, and Legacy Terminal (#9999)
        Assert(fullReport.TerminalBreakdown.Count == 3, $"Expected 3 terminals in breakdown, got {fullReport.TerminalBreakdown.Count}");
        var t1 = fullReport.TerminalBreakdown.Single(t => t.TerminalId == 1);
        Assert(t1.TransactionCount == 1 && t1.VoidedCount == 1 && t1.GrossSales == 1000m && t1.TotalRefunds == 150m && t1.NetSalesAfterRefunds == 850m,
            "Terminal 1 breakdown must reflect 1 paid, 1 void, 1000.00 gross, 150.00 refund, and 850.00 net after refund");
        var t2 = fullReport.TerminalBreakdown.Single(t => t.TerminalId == 2);
        Assert(t2.TransactionCount == 1 && t2.VoidedCount == 0 && t2.GrossSales == 600m && t2.TotalDiscounts == 100m && t2.NetSalesAfterRefunds == 500m,
            "Terminal 2 breakdown must reflect 600.00 gross, 100.00 discount, and 500.00 net");
        var legacyTerm = fullReport.TerminalBreakdown.Single(t => t.TerminalId == 9999);
        Assert(legacyTerm.IsLegacyUnknownTerminal && legacyTerm.TerminalName.Contains("Unknown / Legacy Terminal"),
            "Unmatched TerminalId=9999 must be attributed as Unknown / Legacy Terminal without error");

        // 4. Grouping by TERMINAL in Breakdown
        Assert(fullReport.Breakdown.Count == 3, $"Expected 3 terminal groups in Breakdown, got {fullReport.Breakdown.Count}");
        Assert(fullReport.Breakdown.Any(g => g.TerminalId == 1 && g.NetSalesAfterRefunds == 850m), "Breakdown item for Terminal 1 must compute NetSalesAfterRefunds=850.00");

        // 5. Cascading Outlet Filter: OutletId = 1 only includes Terminal 1 and Terminal 2 orders (Order 804 excluded)
        var outlet1Report = await reportService.GenerateReportAsync(new SalesReportRequestDto { BranchId = 1, OutletId = 1 });
        Assert(outlet1Report.CompletedTransactionsCount == 2, $"Expected 2 paid orders for Outlet 1, got {outlet1Report.CompletedTransactionsCount}");
        Assert(outlet1Report.TerminalBreakdown.All(t => t.OutletId == 1), "Outlet 1 report must only contain Outlet 1 terminals");
    }

    private static async Task Test48_BackOfficeShiftReconciliationAndTerminalFiltering()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connScope = conn;

        // Shift 1 has recorded denominations
        shift.Status = "CLOSED";
        shift.ClosedAt = DateTime.UtcNow;
        shift.ClosingDenominationsJson = "[{\"Type\":\"BILL\",\"Denomination\":1000.0,\"Quantity\":2,\"Subtotal\":2000.0}]";
        shift.ActualCountedBalance = 2000.00m;
        shift.ClosingPaymentReconciliationsJson = "[{\"PaymentMethodCode\":\"CASH\",\"PaymentMethodName\":\"Cash\",\"IsCash\":true,\"ExpectedAmount\":2000.0,\"DeclaredAmount\":2000.0,\"Variance\":0.0,\"Status\":\"BALANCED\"}]";

        // Shift 2 is a historical legacy shift without denomination records and unmatched TerminalId=8888
        var legacyShift = new CashDrawerShift
        {
            ShiftId = 2,
            BranchId = 1,
            TerminalId = 8888, // Unmatched terminal
            CashierId = "cashier2",
            OpeningFloat = 1500.00m,
            OpeningConfirmed = true,
            ExpectedBalance = 1500.00m,
            ActualCountedBalance = 1500.00m,
            Discrepancy = 0m,
            Status = "CLOSED",
            OpenedAt = DateTime.UtcNow.AddHours(-10),
            ClosedAt = DateTime.UtcNow.AddHours(-2),
            ClosingDenominationsJson = null // Historical shift: NO recorded denominations!
        };
        db.CashDrawerShifts.Add(legacyShift);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

        var supController = CreateController(db, "manager1", "Manager");

        // 1. Supervisor queries shifts: verifies reconciliation details and historical denominations
        var shiftsRes = (await supController.GetShifts(branchId: 1)).Result as OkObjectResult;
        Assert(shiftsRes?.Value is List<CashDrawerShiftDto>, "GetShifts must return OkObjectResult with shift list");
        var shiftList = (List<CashDrawerShiftDto>)shiftsRes!.Value!;
        Assert(shiftList.Count == 2, $"Expected 2 shifts, got {shiftList.Count}");

        var s1Dto = shiftList.Single(s => s.ShiftId == 1);
        Assert(s1Dto.HasRecordedDenominations && s1Dto.Denominations.Count == 1,
            "Shift 1 must indicate HasRecordedDenominations=true with 1 denomination line");
        Assert(s1Dto.TerminalName == "Counter 1", $"Expected TerminalName 'Counter 1', got '{s1Dto.TerminalName}'");
        Assert(s1Dto.OverallReconciliationStatus == "BALANCED", "Shift 1 must be BALANCED");

        var s2Dto = shiftList.Single(s => s.ShiftId == 2);
        Assert(!s2Dto.HasRecordedDenominations && s2Dto.Denominations.Count == 0,
            "Historical Shift 2 with ClosingDenominationsJson=null must set HasRecordedDenominations=false and empty Denominations list (not zero-filled)");
        Assert(s2Dto.TerminalName.Contains("Unknown / Legacy Terminal"),
            $"Shift 2 with unmatched TerminalId=8888 must resolve to Unknown / Legacy Terminal, got '{s2Dto.TerminalName}'");

        // 2. Cashier direct query to supervisor endpoints must be forbidden (403)
        var cashierController = CreateController(db, "cashier1", "Cashier");
        var cashierShiftsRes = await cashierController.GetShifts(branchId: 1);
        Assert(cashierShiftsRes.Result is ForbidResult, "Cashier role must be forbidden from GetShifts");
        var cashierSummaryRes = await cashierController.GetMovementSummaryReport(branchId: 1);
        Assert(cashierSummaryRes.Result is ForbidResult, "Cashier role must be forbidden from GetMovementSummaryReport");

        // 3. Supervisor queries GetMovementSummaryReport: verify ByTerminal breakdown
        db.CashDrawerMovements.Add(new CashDrawerMovement
        {
            MovementId = 901, ShiftId = 1, BranchId = 1, TerminalId = 1, MovementType = "CASH_IN", CategoryId = 1,
            Amount = 300.00m, Reason = "Top up float", Status = "POSTED", AuthorizedBy = "manager1", CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var summaryRes = (await supController.GetMovementSummaryReport(branchId: 1)).Result as OkObjectResult;
        Assert(summaryRes?.Value is CashMovementSummaryReportDto, "Expected OkObjectResult with CashMovementSummaryReportDto");
        var summaryDto = (CashMovementSummaryReportDto)summaryRes!.Value!;
        Assert(summaryDto.ByTerminal.Count > 0, "Summary report must include ByTerminal breakdown");
        var termMovement = summaryDto.ByTerminal.Single(t => t.TerminalId == 1);
        Assert(termMovement.TerminalCode == "POS-1" && termMovement.TotalPaidIn == 300.00m,
            "Movement summary terminal breakdown must match terminal code POS-1 and TotalPaidIn 300.00");
    }
}
