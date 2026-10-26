using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Shared.Models;
namespace POS.Tests;
public static partial class Program
{
    private static async Task Test39_OpeningFloatRequired()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var cleanup = conn;
        db.CashDrawerShifts.Remove(shift); await db.SaveChangesAsync();
        var api = CreateController(db);
        var result = await api.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1 });
        Assert(result is BadRequestObjectResult, "Blank float must not implicitly open a zero-float shift.");
        Assert(!await db.CashDrawerShifts.AnyAsync(), "Invalid opening must not persist.");
        var legacy = new POS.Api.Models.CashDrawerShift { BranchId = 1, TerminalId = 1, CashierId = "cashier1", OpeningFloat = 100, ExpectedBalance = 100, Status = "OPEN", OpenedAt = DateTime.UtcNow.AddHours(-2) };
        db.CashDrawerShifts.Add(legacy); await db.SaveChangesAsync();
        var historicalOpenedAt = legacy.OpenedAt;
        Assert(await api.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, OpeningFloat = 25, ClientShiftId = "rewrite-legacy" }) is ConflictObjectResult, "Legacy shift must be reconciled, not rewritten by confirmation.");
        Assert(legacy.OpeningFloat == 100 && legacy.OpenedAt == historicalOpenedAt && !legacy.OpeningConfirmed, "Legacy opening amount and lifecycle must remain unchanged.");
        db.CashDrawerShifts.Remove(legacy); await db.SaveChangesAsync();
        var foreignOpening = System.Text.Json.JsonSerializer.Deserialize<OpenShiftDto>("{\"BranchId\":1,\"TerminalId\":1,\"OpeningFloat\":25,\"CashierId\":\"cashier2\",\"ClientShiftId\":\"foreign-opening\"}")!;
        Assert(await api.OpenShift(foreignOpening) is ForbidResult, "Replayed offline opening must retain its original cashier, not the uploader.");
        Assert(await api.OpenShift(new OpenShiftDto { BranchId = 1, TerminalId = 1, OpeningFloat = -1 }) is BadRequestObjectResult, "Negative rejected.");
        var opening = new OpenShiftDto { BranchId = 1, TerminalId = 1, OpeningFloat = 0, ClientShiftId = "opening-39" };
        Assert(await api.OpenShift(opening) is OkObjectResult, "Explicit zero accepted.");
        Assert(await api.OpenShift(opening) is OkObjectResult && await db.CashDrawerShifts.CountAsync() == 1, "Retry must reuse shift.");
        var retryMismatch = new OpenShiftDto { BranchId = 999, TerminalId = 1, OpeningFloat = 0, ClientShiftId = "opening-39" };
        Assert(await api.OpenShift(retryMismatch) is ConflictObjectResult, "Retry cannot change opening branch ownership.");
        var saved = await db.CashDrawerShifts.SingleAsync();
        Assert(saved.OpeningConfirmed && saved.TotalCashSales == 0 && saved.ExpectedBalance == 0, "Confirmation is audited, never sales revenue.");
        db.Orders.Add(new POS.Api.Models.Order { OrderType = "RETAIL", OrderStatus = "PENDING", PaymentStatus = "UNPAID", OrderNumber = "GATE", BranchId = 1, OutletId = 1, TerminalId = 1, CashierId = "cashier1", TotalAmount = 1 });
        await db.SaveChangesAsync();
        saved.OpeningConfirmed = false; await db.SaveChangesAsync();
        var payments = new POS.Api.Controllers.PaymentsController(db, new POS.Api.Services.SequenceService(db)); Authenticate(payments);
        Assert((await payments.ProcessPayment(new PaymentProcessDto { OrderId = (await db.Orders.SingleAsync()).OrderId })).Result is ConflictObjectResult, "Even API payment requires confirmed float.");
        saved.OpeningConfirmed = true; saved.Status = "CLOSED"; saved.ClosedAt = DateTime.UtcNow; await db.SaveChangesAsync();
        db.InvoiceSequences.Add(new POS.Api.Models.InvoiceSequence { TerminalId = 1, SeriesCode = "HISTORY", Prefix = "H-", StartingNumber = 1, EndingNumber = 100, LastIssuedNumber = 0 }); await db.SaveChangesAsync();
        var sync = new POS.Api.Controllers.OfflineSyncController(db); Authenticate(sync);
        var replay = new OfflineTransactionSyncRequest { LocalTransactionId = "historical-39", InvoiceNumber = "H-0000001", TerminalId = 1, BranchId = 1, CashierId = "cashier1", ClientShiftId = saved.ClientShiftId, TransactionDate = saved.OpenedAt.AddTicks(1), PayloadHash = "history" };
        Assert((await sync.SyncTransaction(replay)).Result is OkObjectResult, "Historical sale during a confirmed closed shift must replay.");
        replay.LocalTransactionId = "after-close-39"; replay.InvoiceNumber = "H-0000002"; replay.TransactionDate = saved.ClosedAt.Value.AddSeconds(1);
        Assert((await sync.SyncTransaction(replay)).Result is ConflictObjectResult, "Sale after close cannot replay into that drawer.");
        var next = new OpenShiftDto { TerminalId = 1, BranchId = 1, OpeningFloat = 50, ClientShiftId = "next-39" };
        Assert(await api.OpenShift(next) is OkObjectResult, "Closing requires and allows a new confirmed opening.");
        Assert(await api.OpenShift(new OpenShiftDto { TerminalId = 1, BranchId = 1, OpeningFloat = 50, ClientShiftId = "duplicate-drawer" }) is ConflictObjectResult, "A drawer cannot have two active shifts.");
        Assert(await CreateController(db, "cashier2").OpenShift(new OpenShiftDto { TerminalId = 1, BranchId = 1, OpeningFloat = 0 }) is ConflictObjectResult, "Another cashier cannot take the open drawer.");
        opening.OpeningFloat = 99;
        Assert(await api.OpenShift(opening) is ConflictObjectResult, "Changed retry payload cannot change historical float.");
        replay.LocalTransactionId = "wrong-owner-39"; replay.InvoiceNumber = "H-0000003"; replay.CashierId = "cashier2"; replay.TransactionDate = saved.OpenedAt.AddTicks(1);
        Assert((await sync.SyncTransaction(replay)).Result is ConflictObjectResult, "Historical shift ownership is required.");
        var current = await db.CashDrawerShifts.SingleAsync(s => s.Status == "OPEN");
        Assert(await CreateController(db, "cashier2").CloseShift(new CloseShiftDto { ShiftId = current.ShiftId }) is ForbidResult, "Other cashier cannot close drawer.");
        current.Status = "CLOSED"; current.ClosedAt = DateTime.UtcNow; await db.SaveChangesAsync();
        Assert((await payments.ProcessPayment(new PaymentProcessDto { OrderId = (await db.Orders.FirstAsync(o => o.OrderNumber == "GATE")).OrderId })).Result is ConflictObjectResult, "Payment after close remains blocked.");
    }
}
