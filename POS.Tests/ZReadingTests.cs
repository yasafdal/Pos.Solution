using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task TestZReadingUnreconciledServerEvents()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connection = conn;
        var date = new DateOnly(2026, 10, 29); var (start, _) = ZReadingService.Period(date);
        shift.Status = "CLOSED"; shift.OpenedAt = start.AddHours(1); shift.ClosedAt = start.AddHours(8);
        db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = date, ConfiguredBy = "verified" });
        var order = new Order { OrderNumber = "Z-UNRECONCILED", BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", OrderType = "DINE_IN", OrderStatus = "COMPLETED", PaymentStatus = "PAID", CreatedAt = start.AddHours(1), InvoiceIssuedAt = start.AddHours(1), InvoiceNumber = "Z-UNRECONCILED", Subtotal = 100, TotalAmount = 100 };
        order.Payments.Add(new Payment { PaymentMethod = "CASH", Amount = 80, PaymentStatus = "PAID", ProcessedBy = "cashier1", PaidAt = start.AddHours(1) });
        db.Orders.Add(order); await db.SaveChangesAsync();
        var service = new ZReadingService(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZReading:Enabled"] = "true" }).Build());
        var ready = await service.ReadinessAsync(1, date);
        Assert(!ready.IsReady && ready.Blockers.Any(x => x.Contains("reconcile", StringComparison.OrdinalIgnoreCase)), "A paid flag without reconciled payment entries cannot finalize revenue.");
        order.Payments.Single().Amount = 100;
        order.OrderStatus = "PENDING"; await db.SaveChangesAsync();
        ready = await service.ReadinessAsync(1, date);
        Assert(!ready.IsReady, "Incomplete order cannot be hidden behind a PAID flag.");
        order.OrderStatus = "COMPLETED"; await db.SaveChangesAsync();
        Assert((await service.ReadinessAsync(1, date)).IsReady, "Reconciled completed order should be ready.");
    }

    private static async Task TestZReadingPrerequisites()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connection = conn;
        var date = new DateOnly(2026, 10, 29);
        var enabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZReading:Enabled"] = "true" }).Build();
        var service = new ZReadingService(db, enabled);
        var ready = await service.ReadinessAsync(1, date);
        Assert(!ready.IsReady && ready.Blockers.Any(x => x.Contains("provisioned")), "No fabricated historical opening baseline.");
        var (start, _) = ZReadingService.Period(date);
        shift.OpenedAt = start.AddHours(1);
        db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = date, ConfiguredBy = "verified" });
        await db.SaveChangesAsync();
        ready = await service.ReadinessAsync(1, date);
        Assert(!ready.IsReady && ready.Blockers.Any(x => x.Contains("shift")), "Open shift blocks finalization.");
        shift.Status = "CLOSED"; shift.ClosedAt = start.AddHours(8);
        await db.SaveChangesAsync();
        var request = new GenerateZReadingRequest { TerminalId = 1, BusinessDate = date, ClientOperationId = "prereq", DeviceIdentifier = "test" };
        try { await service.GenerateAsync(request, "manager1"); throw new Exception("Missing explicit device completeness accepted."); }
        catch (ZReadingException ex) when (ex.StatusCode == 409) { }
        request.AllLocalOutboxesDrained = request.NoUnresolvedLocalTransactions = request.SoleDeviceCompletenessConfirmed = true;
        await service.GenerateAsync(request, "manager1");
        var state = await db.ZReadingTerminalStates.SingleAsync();
        var version = state.GetType().GetProperty("MutationVersion");
        Assert(version != null && (long)version.GetValue(state)! == 1, "Generation must UPDATE the same terminal-state fence as writers, not merely lock a terminal row.");
        var next = await service.ReadinessAsync(1, date.AddDays(2));
        Assert(!next.IsReady && next.NextBusinessDate == date.AddDays(1), "Missing zero-sales business dates cannot be skipped.");
        var disabled = new ZReadingService(db, new ConfigurationBuilder().Build());
        await db.Database.ExecuteSqlRawAsync("DROP TABLE ZReadingPrintResults; DROP TABLE ZReadingPrintAttempts; DROP TABLE ZReadings; DROP TABLE ZReadingTerminalStates;");
        Assert(!(await disabled.ReadinessAsync(1, date)).Enabled, "Disabled readiness must not query absent deployment tables.");
        try { await disabled.GetAsync(1, date); throw new Exception("Disabled retrieval accepted."); }
        catch (ZReadingException ex) when (ex.StatusCode == 409) { }
    }

    private static async Task TestZReadingPrintAudit()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connection = conn;
        var date = new DateOnly(2026, 10, 29); var (start, _) = ZReadingService.Period(date);
        shift.Status = "CLOSED"; shift.OpenedAt = start.AddHours(1); shift.ClosedAt = start.AddHours(8);
        db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = date, ConfiguredBy = "verified" });
        await db.SaveChangesAsync();
        dynamic service = new ZReadingService(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZReading:Enabled"] = "true" }).Build());
        ZReadingReportDto report = await service.GenerateAsync(new GenerateZReadingRequest { TerminalId = 1, BusinessDate = date, ClientOperationId = "print-report", DeviceIdentifier = "test", AllLocalOutboxesDrained = true, NoUnresolvedLocalTransactions = true, SoleDeviceCompletenessConfirmed = true }, "manager1");
        Assert(typeof(ZReadingService).GetMethod("StartPrintAsync") != null, "Separate immutable print-attempt API is missing.");
        ZReadingPrintAttemptDto started = await service.StartPrintAsync(report.Id, new StartZReadingPrintRequest { ClientOperationId = "print-1", PrinterName = "test-printer" }, "manager1");
        Assert(started.Status == "STARTED" && started.Report.Id == report.Id && started.SnapshotSha256.Length == 64, "Persist STARTED and return the saved snapshot before any printing.");
        ZReadingPrintAttemptDto retry = await service.StartPrintAsync(report.Id, new StartZReadingPrintRequest { ClientOperationId = "print-1", PrinterName = "test-printer" }, "manager1");
        Assert(started.Id == retry.Id, "Start retries cannot create a second print attempt.");
        ZReadingPrintAttemptDto finished = await service.CompletePrintAsync(report.Id, started.Id, new CompleteZReadingPrintRequest { Status = "UNKNOWN", ErrorMessage = "dialog" }, "manager1");
        Assert(finished.Status == "UNKNOWN" && finished.CompletedAt != null, "Dialog return never proves acceptance or physical output.");
        try { await service.CompletePrintAsync(report.Id, started.Id, new CompleteZReadingPrintRequest { Status = "ACCEPTED" }, "manager1"); throw new Exception("Completed audit was rewritten."); }
        catch (ZReadingException ex) when (ex.StatusCode == 409) { }
        try { await service.StartPrintAsync(report.Id, new StartZReadingPrintRequest { ClientOperationId = "print-2" }, "manager1"); throw new Exception("Reprint without reason was permitted."); }
        catch (ZReadingException ex) when (ex.StatusCode == 400) { }
        Assert(typeof(ZReadingService).GetMethod("PrintAttemptsAsync") != null, "Append-only print history must be retrievable after restart.");
        ZReadingPrintAttemptDto reprint = await service.StartPrintAsync(report.Id, new StartZReadingPrintRequest { ClientOperationId = "print-2", Reason = "Paper jam recovery" }, "manager1");
        var history = await ((ZReadingService)service).PrintAttemptsAsync(report.Id);
        Assert(history.Count == 2 && history[0].Status == "UNKNOWN" && history[1].IsReprint, "Restart history retains original outcome and separate reprint attempt.");
        Assert(reprint.Report.Id == report.Id && await db.ZReadings.CountAsync() == 1 && (await db.ZReadingTerminalStates.SingleAsync()).LastZNumber == 1, "Reprints never regenerate snapshots or advance counters.");
    }

    private static async Task TestZReadingControllerScope()
    {
        var type = typeof(ShiftLifecycle).Assembly.GetType("POS.Api.Controllers.ZReadingsController");
        Assert(type != null, "Supervisor-only scoped ZReadingsController is missing.");
        var (conn, db, _) = await CreateTestEnvAsync();
        await using var connection = conn;
        var config = new ConfigurationBuilder().Build();
        dynamic controller = Activator.CreateInstance(type!, db, config)!;
        var identity = new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name, "manager1"), new(System.Security.Claims.ClaimTypes.Role, "Manager"), new("branch_id", "1"), new("terminal_id", "2")], "test");
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } };
        object denied = await controller.Generate(new GenerateZReadingRequest { TerminalId = 1, BusinessDate = new DateOnly(2026, 10, 29) }, CancellationToken.None);
        Assert(denied is Microsoft.AspNetCore.Mvc.ForbidResult, "Even a manager cannot finalize a different current terminal.");
        identity = new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name, "cashier1"), new(System.Security.Claims.ClaimTypes.Role, "Cashier"), new("branch_id", "1"), new("terminal_id", "1")], "test");
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } };
        denied = await controller.Readiness(1, new DateOnly(2026, 10, 29), CancellationToken.None);
        Assert(denied is Microsoft.AspNetCore.Mvc.ForbidResult, "Plain cashier cannot read supervisor reports even when terminal scoped.");
        identity = new System.Security.Claims.ClaimsIdentity([new(System.Security.Claims.ClaimTypes.Name, "senior"), new(System.Security.Claims.ClaimTypes.Role, "Senior Cashier"), new("terminal_id", "1")], "test");
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = new System.Security.Claims.ClaimsPrincipal(identity) } };
        denied = await controller.Readiness(1, new DateOnly(2026, 10, 29), CancellationToken.None);
        Assert(denied is Microsoft.AspNetCore.Mvc.ForbidResult, "Missing branch claim fails closed.");
    }

    private static async Task TestZReadingEvents()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connection = conn;
        var date = new DateOnly(2026, 10, 29);
        var (start, end) = ZReadingService.Period(date);
        shift.Status = "CLOSED"; shift.OpenedAt = start.AddHours(1); shift.ClosedAt = start.AddHours(8);
        db.ZReadingTerminalStates.Add(new ZReadingTerminalState { TerminalId = 1, FirstBusinessDate = date, OpeningAccumulatedSales = 10m, AccumulatedGrossSales = 10m, ConfiguredBy = "verified" });
        var old = new Order { OrderId = 701, OrderNumber = "Z-OLD", TerminalId = 1, OutletId = 1, BranchId = 1, CashierId = "cashier1", ShiftId = 1, OrderType = "DINE_IN", OrderStatus = "COMPLETED", PaymentStatus = "PAID", CreatedAt = start.AddDays(-1), InvoiceIssuedAt = start.AddDays(-1), InvoiceNumber = "ZI-OLD", Subtotal = 56m, TotalAmount = 56m, VatableSales = 50m, VatAmount = 6m };
        old.OrderItems.Add(new OrderItem { OrderItemId = 701, ProductId = 1, ProductName = "Historical coffee", Quantity = .5m, UnitPrice = 112m, LineTotal = 56m, VatAmount = 6m });
        old.Payments.Add(new Payment { PaymentId = 701, PaymentMethod = "CASH", Amount = 56m, PaymentStatus = "REFUNDED", ProcessedBy = "cashier1", PaidAt = start.AddDays(-1) });
        var sale = new Order { OrderId = 702, OrderNumber = "Z-NEW", TerminalId = 1, OutletId = 1, BranchId = 1, CashierId = "cashier1", ShiftId = 1, OrderType = "DINE_IN", OrderStatus = "VOIDED", PaymentStatus = "VOIDED", CreatedAt = start.AddHours(2), InvoiceIssuedAt = start.AddHours(2), InvoiceNumber = "ZI-NEW", Subtotal = 112m, DiscountAmount = 12m, TotalAmount = 100m, VatExemptSales = 100m, DiscountCode = "SC", DiscountName = "Senior" };
        sale.OrderItems.Add(new OrderItem { OrderItemId = 702, ProductId = 1, ProductName = "Historical coffee", Quantity = 1m, UnitPrice = 112m, LineTotal = 112m, DiscountAmount = 12m, VatType = "VAT_EXEMPT" });
        sale.Payments.Add(new Payment { PaymentId = 702, PaymentMethod = "CASH", Amount = 60m, PaymentStatus = "REFUNDED", ProcessedBy = "cashier1", PaidAt = start.AddHours(2) });
        sale.Payments.Add(new Payment { PaymentId = 703, PaymentMethod = "CARD", Amount = 40m, PaymentStatus = "REFUNDED", ProcessedBy = "cashier1", PaidAt = start.AddHours(2) });
        db.Orders.AddRange(old, sale);
        var ret = new SalesReturn { ReturnId = 701, OriginalOrderId = old.OrderId, BranchId = 1, TerminalId = 1, ReturnReference = "ZR-OLD", ReturnReason = "test", RequestedBy = "cashier2", Status = "COMPLETED", TotalRefundAmount = 28m, CreatedAt = start.AddHours(3), CompletedAt = start.AddHours(3) };
        ret.ReturnItems.Add(new SalesReturnItem { OriginalOrderItemId = 701, Quantity = .25m, OriginalUnitPrice = 112m, RefundAmount = 28m, AllocatedTax = 3m });
        ret.Refunds.Add(new Refund { OriginalPaymentId = 701, RefundAmount = 28m, RefundMethod = "CASH", CompletedAt = start.AddHours(3), RequestedAt = start.AddHours(3), Status = "COMPLETED" });
        db.SalesReturns.Add(ret);
        var v = new TransactionVoid { VoidId = 701, OriginalOrderId = sale.OrderId, RequestedBy = "cashier2", ApprovedBy = "manager1", VoidReason = "test", VoidedAt = start.AddHours(4), Status = "APPROVED" };
        v.Refunds.Add(new Refund { OriginalPaymentId = 702, RefundAmount = 60m, RefundMethod = "CASH", CompletedAt = start.AddHours(4), RequestedAt = start.AddHours(4) });
        v.Refunds.Add(new Refund { OriginalPaymentId = 703, RefundAmount = 40m, RefundMethod = "CARD", CompletedAt = start.AddHours(4), RequestedAt = start.AddHours(4) });
        db.TransactionVoids.Add(v);
        await db.SaveChangesAsync();
        var service = new ZReadingService(db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZReading:Enabled"] = "true" }).Build());
        var r = await service.GenerateAsync(new GenerateZReadingRequest { TerminalId = 1, BusinessDate = date, ClientOperationId = "events", DeviceIdentifier = "test", AllLocalOutboxesDrained = true, NoUnresolvedLocalTransactions = true, SoleDeviceCompletenessConfirmed = true }, "manager1");
        Assert(r.GrossSales == 112m && r.Discounts == 12m && r.Returns == 28m && r.Voids == 100m && r.NetSales == -28m, "Include original sale even after void; subtract adjustment once in actual event day, not old sale day.");
        Assert(r.Products.Single().Quantity == 1m && r.ExemptProducts.Single().Value == 112m && r.ReturnProducts.Single().Quantity == .25m, "Snapshot product detail preserves names and decimal quantities.");
        Assert(r.Cashiers.Single(x => x.CashierId == "cashier1").GrossSales == 112m && r.Cashiers.Single(x => x.CashierId == "cashier2").NetSales == -128m, "Adjustments attributed to processing cashier.");
        Assert(r.Cashiers.Single(x => x.CashierId == "cashier1").ItemQuantity == 1m && r.Cashiers.Single(x => x.CashierId == "cashier2").ReturnQuantity == .25m && r.Cashiers.Single(x => x.CashierId == "cashier2").VoidQuantity == 1m, "Cashier item counts use decimal quantities, not payment counts.");
        Assert(r.Payments.Sum(x => x.Collections) == 100m && r.Payments.Sum(x => x.Refunds) == 128m && r.Payments.Sum(x => x.PaymentCount) == 2, "Split refunded payments remain original collections; refunds are separate event entries.");
        Assert(r.FirstRefundId == ret.Refunds.Single().RefundId && r.LastRefundId == v.Refunds.Max(x => x.RefundId), "Refund bounds are actual saved refund IDs, not invented fiscal numbers.");
        Assert(r.PresentAccumulatedSales == 122m && r.ReturnVatAmount == 3m && r.NetVatAmount == -3m, "Cumulative gross is not reset or reduced by refunds; VAT reversal retains stored allocation.");
        Assert(!r.HistoricalItemVatPrecisionAvailable && r.Warnings.Count > 0, "Legacy item VAT provenance is explicitly uncertain.");
    }

    private static async Task TestZReadingGeneration()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var connection = conn;
        shift.Status = "CLOSED";
        var date = new DateOnly(2026, 10, 29);
        shift.OpenedAt = new DateTime(2026, 10, 28, 21, 0, 0);
        shift.ClosedAt = shift.OpenedAt.AddHours(4);
        await db.SaveChangesAsync();
        var type = typeof(ShiftLifecycle).Assembly.GetType("POS.Api.Models.ZReadingTerminalState");
        Assert(type != null, "Explicit provisioned terminal state is missing.");
        dynamic state = Activator.CreateInstance(type!)!;
        state.TerminalId = 1;
        state.FirstBusinessDate = date;
        state.OpeningAccumulatedSales = 123m;
        state.AccumulatedGrossSales = 123m;
        state.ConfiguredBy = "DBA verified opening";
        db.Add((object)state);
        await db.SaveChangesAsync();
        var serviceType = typeof(ZReadingService);
        var generate = serviceType.GetMethod("GenerateAsync");
        Assert(generate != null, "Atomic immutable generation API is missing.");
        dynamic service = Activator.CreateInstance(serviceType, db, new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ZReading:Enabled"] = "true" }).Build())!;
        var request = new GenerateZReadingRequest { TerminalId = 1, BusinessDate = date, ClientOperationId = "z-op-1", AllLocalOutboxesDrained = true, NoUnresolvedLocalTransactions = true, SoleDeviceCompletenessConfirmed = true, DeviceIdentifier = "test-device" };
        ZReadingReportDto report = await service.GenerateAsync(request, "manager1");
        ZReadingReportDto retry = await service.GenerateAsync(request, "manager1");
        Assert(report.Id == retry.Id && report.ZNumber == 1 && report.PreviousAccumulatedSales == 123m && report.PresentAccumulatedSales == 123m, "Retry must read same snapshot and increment sequence only once, including zero-sales days.");
        Assert(report.ResetCounter == null, "Unconfigured fiscal reset counter must remain unknown.");
        var entity = db.Model.GetEntityTypes().Single(e => e.ClrType.Name == "ZReading");
        var tracked = db.ChangeTracker.Entries().Single(e => e.Entity.GetType() == entity.ClrType);
        tracked.Property("SnapshotJson").CurrentValue = "{}";
        try { await db.SaveChangesAsync(); throw new Exception("Snapshot mutation was permitted."); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("immutable", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static Task TestZReadingBusinessDate()
    {
        var type = typeof(ShiftLifecycle).Assembly.GetType("POS.Api.Services.ZReadingService");
        Assert(type != null, "ZReadingService is missing.");
        var method = type!.GetMethod("BusinessDate", BindingFlags.Public | BindingFlags.Static);
        Assert(method != null, "Public fixed Manila business-date helper is missing.");
        Assert((DateOnly)method!.Invoke(null, [new DateTime(2026, 10, 30, 19, 59, 59, DateTimeKind.Utc)])! == new DateOnly(2026, 10, 30), "03:59 Manila belongs to previous business date.");
        Assert((DateOnly)method.Invoke(null, [new DateTime(2026, 10, 30, 20, 0, 0, DateTimeKind.Utc)])! == new DateOnly(2026, 10, 31), "04:00 Manila starts new business date.");
        return Task.CompletedTask;
    }
}
