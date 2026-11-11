using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task TestPaymentStatusStandardization()
    {
        var (conn, db, shift) = await CreateTestEnvAsync();
        await using var cleanup = conn;

        var seqService = new SequenceService(db);
        var paymentsController = new PaymentsController(db, seqService);
        SetUserContext(paymentsController, "cashier1", "Cashier", branchId: 1, terminalId: 1);

        db.InvoiceSequences.Add(new InvoiceSequence
        {
            InvoiceSequenceId = 1,
            TerminalId = 1,
            SeriesCode = "INV2026",
            Prefix = "SI-",
            StartingNumber = 1000,
            EndingNumber = 100000,
            LastIssuedNumber = 1000,
            PaddingLength = 7,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // 1. Online payment creation sets PaymentStatus = "PAID"
        var order1 = new Order
        {
            OrderId = 1001, BranchId = 1, OutletId = 1, TerminalId = 1, ShiftId = shift.ShiftId,
            OrderNumber = "ORD-1001", OrderType = "DINE_IN", CashierId = "cashier1",
            OrderStatus = "PENDING", PaymentStatus = "UNPAID", Subtotal = 500m, TotalAmount = 500m,
            CreatedAt = DateTime.UtcNow
        };
        db.Orders.Add(order1);
        await db.SaveChangesAsync();

        var payResult = await paymentsController.ProcessPayment(new PaymentProcessDto
        {
            OrderId = 1001,
            PaymentMethod = "CASH",
            Amount = 500m,
            AmountTendered = 500m,
            ProcessedBy = "cashier1"
        });
        Assert(payResult.Result is OkObjectResult, "ProcessPayment must succeed.");
        var order1FromDb = await db.Orders.Include(o => o.Payments).FirstAsync(o => o.OrderId == 1001);
        Assert(order1FromDb.PaymentStatus == PaymentStatuses.Paid, $"Order PaymentStatus must be PAID, got {order1FromDb.PaymentStatus}");
        Assert(order1FromDb.Payments.Count == 1, "Order must have 1 payment.");
        Assert(order1FromDb.Payments.First().PaymentStatus == PaymentStatuses.Paid, $"Payment PaymentStatus must be PAID, got {order1FromDb.Payments.First().PaymentStatus}");

        // 2. Offline sync mapping: incoming legacy "SUCCESS" payload is converted to canonical "PAID"
        var syncController = new OfflineSyncController(db);
        SetUserContext(syncController, "cashier1", "Cashier", branchId: 1, terminalId: 1);

        var syncRequestLegacy = new OfflineTransactionSyncRequest
        {
            TerminalId = 1,
            BranchId = 1,
            ShiftId = shift.ShiftId,
            CashierId = "cashier1",
            LocalTransactionId = Guid.NewGuid().ToString(),
            OrderNumber = "ORD-SYNC-LEGACY",
            InvoiceNumber = "SI-0001002",
            TransactionDate = DateTime.UtcNow,
            Subtotal = 200m,
            TotalAmount = 200m,
            Items = new List<OfflineTransactionItemDto>
            {
                new() { ProductId = 1, ProductName = "Coffee", Quantity = 2, UnitPrice = 100m, LineTotal = 200m, TaxName = "VAT" }
            },
            Payments = new List<OfflinePaymentDto>
            {
                new() { PaymentMethod = "GCASH", Amount = 200m, TenderedAmount = 200m, ChangeAmount = 0m, PaymentStatus = "SUCCESS", PaidAt = DateTime.UtcNow }
            },
            IdempotencyKey = Guid.NewGuid().ToString(),
            PayloadHash = "hash1"
        };

        var syncResLegacy = await syncController.SyncTransaction(syncRequestLegacy);
        var syncErr = (syncResLegacy.Result as ObjectResult)?.Value?.ToString() ?? syncResLegacy.Result?.ToString() ?? "null";
        Assert(syncResLegacy.Result is OkObjectResult, $"SyncTransaction must succeed for legacy payload. Error: {syncErr}");
        var legacyOrderFromDb = await db.Orders.Include(o => o.Payments).FirstAsync(o => o.OrderNumber == "ORD-SYNC-LEGACY");
        Assert(legacyOrderFromDb.PaymentStatus == PaymentStatuses.Paid, "Legacy order PaymentStatus must be PAID.");
        Assert(legacyOrderFromDb.Payments.First().PaymentStatus == PaymentStatuses.Paid,
            $"Legacy payment with status 'SUCCESS' must be stored as 'PAID', got {legacyOrderFromDb.Payments.First().PaymentStatus}");

        // 3. Offline sync mapping: incoming "PAID" payload is preserved as "PAID"
        var syncRequestPaid = new OfflineTransactionSyncRequest
        {
            TerminalId = 1,
            BranchId = 1,
            ShiftId = shift.ShiftId,
            CashierId = "cashier1",
            LocalTransactionId = Guid.NewGuid().ToString(),
            OrderNumber = "ORD-SYNC-PAID",
            InvoiceNumber = "SI-0001003",
            TransactionDate = DateTime.UtcNow,
            Subtotal = 150m,
            TotalAmount = 150m,
            Items = new List<OfflineTransactionItemDto>
            {
                new() { ProductId = 1, ProductName = "Coffee", Quantity = 1, UnitPrice = 150m, LineTotal = 150m, TaxName = "VAT" }
            },
            Payments = new List<OfflinePaymentDto>
            {
                new() { PaymentMethod = "CASH", Amount = 150m, TenderedAmount = 200m, ChangeAmount = 50m, PaymentStatus = PaymentStatuses.Paid, PaidAt = DateTime.UtcNow }
            },
            IdempotencyKey = Guid.NewGuid().ToString(),
            PayloadHash = "hash2"
        };

        var syncResPaid = await syncController.SyncTransaction(syncRequestPaid);
        Assert(syncResPaid.Result is OkObjectResult, "SyncTransaction must succeed for PAID payload.");
        var paidOrderFromDb = await db.Orders.Include(o => o.Payments).FirstAsync(o => o.OrderNumber == "ORD-SYNC-PAID");
        Assert(paidOrderFromDb.PaymentStatus == PaymentStatuses.Paid, "Order PaymentStatus must be PAID.");
        Assert(paidOrderFromDb.Payments.First().PaymentStatus == PaymentStatuses.Paid, "Payment PaymentStatus must be PAID.");

        // 4. Repeated sync is idempotent and does not alter payment status or duplicate rows
        var syncResDuplicate = await syncController.SyncTransaction(syncRequestPaid);
        Assert(syncResDuplicate.Result is OkObjectResult, "Duplicate SyncTransaction must return 200 OK.");
        var countForPaidOrder = await db.Orders.CountAsync(o => o.OrderNumber == "ORD-SYNC-PAID");
        Assert(countForPaidOrder == 1, "Duplicate sync must not duplicate order.");
        var paymentsForPaidOrder = await db.Payments.CountAsync(p => p.OrderId == paidOrderFromDb.OrderId);
        Assert(paymentsForPaidOrder == 1, "Duplicate sync must not duplicate payment.");

        // 5. Voiding an order reverses PAID payments to REFUNDED
        var returnsController = new ReturnsController(db);
        SetUserContext(returnsController, "manager1", "Manager", branchId: 1, terminalId: 1);

        var voidResult = await returnsController.VoidTransaction(new VoidRequestDto
        {
            OriginalOrderId = order1.OrderId,
            VoidReason = "Customer cancellation",
            ApprovedByPinOrUsername = "9999"
        });
        Assert(voidResult.Result is OkObjectResult, "VoidTransaction must succeed.");
        var voidedOrder = await db.Orders.Include(o => o.Payments).FirstAsync(o => o.OrderId == 1001);
        Assert(voidedOrder.OrderStatus == "VOIDED" && voidedOrder.PaymentStatus == "VOIDED", "Voided order status must be VOIDED.");
        Assert(voidedOrder.Payments.First().PaymentStatus == PaymentStatuses.Refunded,
            $"Voided payment must have status REFUNDED, got {voidedOrder.Payments.First().PaymentStatus}");

        // 6. ReceiptService generates receipt for PAID order
        var receiptService = new ReceiptService(db);
        var receipt = await receiptService.GetReceiptAsync(paidOrderFromDb.OrderId, false);
        Assert(receipt != null, "ReceiptService must successfully generate receipt for PAID order.");
        Assert(receipt!.Payments.Count == 1, "Receipt must include payment.");

        // 7. Data migration: existing historical SUCCESS rows are updated to PAID
        var histOrder = new Order
        {
            OrderId = 9999,
            BranchId = 1,
            OutletId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            OrderNumber = "HIST-1",
            OrderType = "DINE_IN",
            OrderStatus = "COMPLETED",
            PaymentStatus = "SUCCESS",
            Subtotal = 100,
            TotalAmount = 100,
            CreatedAt = DateTime.UtcNow
        };
        var histPayment = new Payment
        {
            PaymentId = 9999,
            OrderId = 9999,
            PaymentMethod = "CASH",
            Amount = 100,
            AmountTendered = 100,
            ChangeAmount = 0,
            PaymentStatus = "SUCCESS",
            ProcessedBy = "cashier1",
            PaidAt = DateTime.UtcNow
        };
        db.Orders.Add(histOrder);
        db.Payments.Add(histPayment);
        await db.SaveChangesAsync();

        // Run data migration SQL
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Payments\" SET \"PaymentStatus\" = 'PAID' WHERE \"PaymentStatus\" = 'SUCCESS';");
        await db.Database.ExecuteSqlRawAsync("UPDATE \"Orders\" SET \"PaymentStatus\" = 'PAID' WHERE \"PaymentStatus\" = 'SUCCESS';");

        var migratedOrder = await db.Orders.AsNoTracking().FirstAsync(o => o.OrderId == 9999);
        var migratedPayment = await db.Payments.AsNoTracking().FirstAsync(p => p.PaymentId == 9999);
        Assert(migratedOrder.PaymentStatus == PaymentStatuses.Paid, "Historical order must be migrated to PAID.");
        Assert(migratedPayment.PaymentStatus == PaymentStatuses.Paid, "Historical payment must be migrated to PAID.");

        // Clean up scratch rows
        db.Payments.Remove(histPayment);
        db.Orders.Remove(histOrder);
        await db.SaveChangesAsync();
    }
}
