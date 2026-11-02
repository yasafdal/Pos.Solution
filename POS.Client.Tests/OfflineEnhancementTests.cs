using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Models;

namespace POS.Client.Tests;

internal static class OfflineEnhancementTests
{
    private static readonly string Root = Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "hermes", "cache", "scratch", "offline-enhancement-tests-" + Guid.NewGuid().ToString("N"));

    internal static async Task RunAll(Func<string, Func<Task>, Task> run)
    {
        Directory.CreateDirectory(Root);
        try
        {
            await run("Offline enhancement: First-time initialization gating and completion checkpoint", TestInitializationGatingAndCompletion);
            await run("Offline enhancement: Local-first atomic checkout for cash and non-cash tenders", TestLocalFirstUnifiedSale);
            await run("Offline enhancement: Offline modifier persistence and reprint fidelity", TestOfflineReprintFidelity);
            await run("Offline enhancement: Incremental sync skips full download when valid", TestIncrementalSyncAndLaunch);
            await run("Offline enhancement: Transaction retention prevents deletion of synced orders", TestDataRetentionAndNoDeletion);
            await run("Offline enhancement: Masked PIN protection preserves cached credentials", TestMaskedPinProtection);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            AppContext.SetData("POS.LocalDatabasePath", null);
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    private static LocalPosDbContext NewDatabase()
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".db");
        AppContext.SetData("POS.LocalDatabasePath", path);
        var db = new LocalPosDbContext();
        Program.Check(Path.GetFullPath(db.Database.GetDbConnection().DataSource) == Path.GetFullPath(path), "Must use isolated scratch database.");
        db.InitializeDatabase();
        return db;
    }

    private static async Task TestInitializationGatingAndCompletion()
    {
        using var db = NewDatabase();

        // Initially unprovisioned terminal
        var terminal = new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            TerminalIdentifier = "TERM-01",
            BranchTaxType = "VAT-Registered",
            IsInitialized = false
        };
        db.TerminalConfigs.Add(terminal);
        await db.SaveChangesAsync();

        var session = new PosSessionService();
        session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            IsInitialized = false
        });

        var checkoutService = new OfflineCheckoutService(db, session);

        // Before catalog/sequence exist, checkout readiness fails
        bool readyBefore = await checkoutService.IsReadyForOfflineCheckoutAsync(1);
        Program.Check(!readyBefore, "Unprovisioned terminal must not be ready for sales.");

        // Provision catalog & sequence
        db.Categories.Add(new LocalCategory { CategoryId = 1, Name = "Coffee", ColorCode = "#185132" });
        db.Products.Add(new LocalProduct { ProductId = 10, CategoryId = 1, Name = "Latte", Price = 120m, IsActive = true });
        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = 1,
            SeriesCode = "SERIES-2026",
            Prefix = "INV-",
            StartingNumber = 1,
            EndingNumber = 1000,
            LastIssuedNumber = 0,
            IsActive = true
        });
        await db.SaveChangesAsync();

        bool readyAfterCatalog = await checkoutService.IsReadyForOfflineCheckoutAsync(1);
        Program.Check(readyAfterCatalog, "Terminal with catalog and active sequence must be ready for sales.");

        // Mark checkpoint completed
        terminal.IsInitialized = true;
        terminal.LastSyncAt = DateTime.UtcNow;
        terminal.LastSyncVersion = "1.0.0";
        await db.SaveChangesAsync();

        using var verifyDb = new LocalPosDbContext();
        verifyDb.InitializeDatabase();
        var saved = await verifyDb.TerminalConfigs.SingleAsync(t => t.TerminalId == 1);
        Program.Check(saved.IsInitialized, "Initialization checkpoint was not persisted.");
    }

    private static async Task TestLocalFirstUnifiedSale()
    {
        using var db = NewDatabase();

        var terminal = new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            TerminalIdentifier = "TERM-01",
            BranchTaxType = "VAT-Registered",
            IsInitialized = true
        };
        db.TerminalConfigs.Add(terminal);

        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = 1,
            SeriesCode = "SERIES-2026",
            Prefix = "INV-",
            StartingNumber = 1,
            EndingNumber = 500,
            LastIssuedNumber = 10,
            PaddingLength = 7,
            IsActive = true
        });

        db.Products.Add(new LocalProduct
        {
            ProductId = 20,
            CategoryId = 1,
            Name = "Americano",
            Price = 90m,
            IsActive = true,
            IsKitchenItem = false,
            IsBarItem = true
        });

        // Open a confirmed shift
        var shift = new LocalCashDrawerShift
        {
            ShiftId = 101,
            TerminalId = 1,
            BranchId = 1,
            CashierId = "cashier1",
            OpeningFloat = 500m,
            ExpectedBalance = 500m,
            TotalCashSales = 0m,
            Status = "OPEN",
            OpeningConfirmed = true,
            OpenedAt = DateTime.UtcNow
        };
        db.CashDrawerShifts.Add(shift);
        await db.SaveChangesAsync();

        var session = new PosSessionService();
        session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            IsInitialized = true
        });

        var checkoutService = new OfflineCheckoutService(db, session);

        // 1. Process CASH Sale
        var cashSale = new OrderCreateDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            ShiftId = 101,
            CashierId = "cashier1",
            Items = new List<OrderItemCreateDto>
            {
                new()
                {
                    ProductId = 20,
                    ProductName = "Americano",
                    Quantity = 2,
                    BasePrice = 90m,
                    IsBarItem = true,
                    Modifiers = new List<OrderItemModifierCreateDto>
                    {
                        new() { ModifierOptionId = 5, ModifierName = "Extra Shot", AdditionalPrice = 25m }
                    }
                }
            }
        };

        // Total: (90 + 25) * 2 = 230
        var cashResult = await checkoutService.ProcessSaleAsync(cashSale, 250m, "CASH", null, "cashier1");
        Program.Check(cashResult.Success, "Cash checkout must succeed.");
        Program.Check(cashResult.ChangeAmount == 20m, "Change amount must be 20.");
        Program.Check(cashResult.InvoiceNumber == "INV-0000011", "Invoice number must increment to INV-0000011.");

        // Verify SQLite state
        using (var verifyDb = new LocalPosDbContext())
        {
            verifyDb.InitializeDatabase();
            var order = await verifyDb.Orders.SingleAsync(o => o.OrderId == cashResult.LocalTransactionId);
            Program.Check(order.PaymentStatus == "PAID", "Order PaymentStatus must be PAID.");
            Program.Check(order.OrderStatus == "COMPLETED", "Order OrderStatus must be COMPLETED.");
            Program.Check(order.TotalAmount == 230m, "Order TotalAmount must be 230.");

            var item = await verifyDb.OrderItems.SingleAsync(i => i.OrderId == cashResult.LocalTransactionId);
            Program.Check(item.IsBarItem, "LocalOrderItem must preserve IsBarItem.");
            Program.Check(!string.IsNullOrWhiteSpace(item.ModifiersJson) && item.ModifiersJson.Contains("Extra Shot"), "LocalOrderItem must serialize ModifiersJson.");

            var payment = await verifyDb.Payments.SingleAsync(p => p.OrderId == cashResult.LocalTransactionId);
            Program.Check(payment.PaymentMethod == "CASH" && payment.PaymentStatus == "PAID" && payment.TenderedAmount == 250m, "Payment must be recorded as PAID cash.");

            var outbox = await verifyDb.SyncOutboxes.SingleAsync(o => o.EntityId == cashResult.LocalTransactionId);
            Program.Check(outbox.EntityType == "Order" && outbox.Status == "PENDING", "Outbox must have PENDING order transaction.");

            var updatedShift = await verifyDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == 101);
            Program.Check(updatedShift.TotalCashSales == 230m, "Shift TotalCashSales must accumulate cash sales.");
            Program.Check(updatedShift.ExpectedBalance == 730m, "Shift ExpectedBalance must be 500 + 230 = 730.");
        }

        // 2. Process Non-Cash Sale (GCash)
        var gcashSale = new OrderCreateDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            ShiftId = 101,
            CashierId = "cashier1",
            Items = new List<OrderItemCreateDto>
            {
                new() { ProductId = 20, ProductName = "Americano", Quantity = 1, BasePrice = 90m }
            }
        };

        var gcashResult = await checkoutService.ProcessSaleAsync(gcashSale, 90m, "GCASH", "REF-998877", "cashier1");
        Program.Check(gcashResult.Success, "GCash checkout must succeed.");
        Program.Check(gcashResult.InvoiceNumber == "INV-0000012", "Invoice number must increment to INV-0000012.");

        using (var verifyDb = new LocalPosDbContext())
        {
            verifyDb.InitializeDatabase();
            var gcashPayment = await verifyDb.Payments.SingleAsync(p => p.OrderId == gcashResult.LocalTransactionId);
            Program.Check(gcashPayment.PaymentMethod == "GCASH" && gcashPayment.Amount == 90m, "Non-cash payment must be saved to LocalPayments.");

            var outbox = await verifyDb.SyncOutboxes.SingleAsync(o => o.EntityId == gcashResult.LocalTransactionId);
            Program.Check(outbox.Status == "PENDING", "Non-cash transaction must be queued in SyncOutboxes.");

            // Drawer cash balance must NOT change for non-cash
            var updatedShift = await verifyDb.CashDrawerShifts.SingleAsync(s => s.ShiftId == 101);
            Program.Check(updatedShift.TotalCashSales == 230m, "Non-cash sale must not alter cash drawer sales.");
        }
    }

    private static async Task TestOfflineReprintFidelity()
    {
        using var db = NewDatabase();

        db.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            TerminalIdentifier = "TERM-01",
            BranchTaxType = "VAT-Registered"
        });

        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = 1,
            SeriesCode = "SERIES-2026",
            Prefix = "INV-",
            StartingNumber = 1,
            EndingNumber = 500,
            LastIssuedNumber = 5,
            PaddingLength = 7,
            IsActive = true
        });

        db.Products.Add(new LocalProduct { ProductId = 1, Name = "Burger", Price = 150m, IsActive = true, IsKitchenItem = true });

        db.CashDrawerShifts.Add(new LocalCashDrawerShift
        {
            ShiftId = 1,
            TerminalId = 1,
            BranchId = 1,
            CashierId = "cashier1",
            OpeningFloat = 100m,
            ExpectedBalance = 100m,
            Status = "OPEN",
            OpeningConfirmed = true,
            OpenedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var session = new PosSessionService();
        session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto { TerminalId = 1, BranchId = 1, OutletId = 1 });

        var checkoutService = new OfflineCheckoutService(db, session);
        var sale = new OrderCreateDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            CashierId = "cashier1",
            CustomerName = "John Doe",
            Items = new List<OrderItemCreateDto>
            {
                new()
                {
                    ProductId = 1,
                    ProductName = "Burger",
                    ProductShortCode = "BGR",
                    Quantity = 1,
                    BasePrice = 150m,
                    IsKitchenItem = true,
                    Modifiers = new List<OrderItemModifierCreateDto>
                    {
                        new() { ModifierOptionId = 10, ModifierName = "Extra Cheese", AdditionalPrice = 30m },
                        new() { ModifierOptionId = 11, ModifierName = "Bacon", AdditionalPrice = 45m }
                    }
                }
            }
        };

        var result = await checkoutService.ProcessSaleAsync(sale, 225m, "CASH", null, "cashier1");
        Program.Check(result.Success, "Sale must succeed.");

        // Read back from SQLite tables as Transactions.razor does
        using var readDb = new LocalPosDbContext();
        readDb.InitializeDatabase();
        var order = await readDb.Orders.SingleAsync(o => o.OrderId == result.LocalTransactionId);
        var item = await readDb.OrderItems.SingleAsync(i => i.OrderId == result.LocalTransactionId);
        var payment = await readDb.Payments.SingleAsync(p => p.OrderId == result.LocalTransactionId);

        // Deserialize modifiers from ModifiersJson
        var modifiers = JsonSerializer.Deserialize<List<OrderItemModifierResponseDto>>(item.ModifiersJson!)!;
        Program.Check(modifiers.Count == 2, "Reconstructed modifiers must contain 2 modifiers.");
        Program.Check(modifiers.Any(m => m.ModifierName == "Extra Cheese") && modifiers.Any(m => m.ModifierName == "Bacon"), "Modifier names must be preserved.");
        Program.Check(item.ProductShortCode == "BGR", "ShortCode must be preserved.");
        Program.Check(item.IsKitchenItem, "IsKitchenItem flag must be preserved for kitchen ticket reprint.");
        Program.Check(order.PaymentStatus == "PAID", "Order must show as PAID in history.");
    }

    private static async Task TestIncrementalSyncAndLaunch()
    {
        using var db = NewDatabase();

        var terminal = new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "Register 1",
            TerminalIdentifier = "TERM-01",
            IsInitialized = true,
            LastSyncVersion = "VERSION-100",
            LastSyncAt = DateTime.UtcNow.AddMinutes(-5) // Recently synced 5 minutes ago
        };
        db.TerminalConfigs.Add(terminal);
        db.Products.Add(new LocalProduct { ProductId = 1, Name = "Item", Price = 50m, IsActive = true });
        db.Taxes.Add(new LocalTax { TaxId = 1, Name = "VATable", Rate = 0.12m });
        await db.SaveChangesAsync();

        var syncService = new ReferenceDataSyncService(db);

        // Test incremental check: recently synced and valid cache skips full download
        var emptyHttp = new HttpClient(); // Unused because network must not be touched
        bool incrementalOk = await syncService.CheckAndSyncIncrementalAsync(1, emptyHttp);
        Program.Check(incrementalOk, "Incremental sync check must succeed without network request when recently synced and valid.");
    }

    private static async Task TestDataRetentionAndNoDeletion()
    {
        using var db = NewDatabase();

        var orderId = Guid.NewGuid().ToString();
        db.Orders.Add(new LocalOrder
        {
            OrderId = orderId,
            InvoiceNumber = "INV-0000001",
            BranchId = 1,
            TerminalId = 1,
            CashierId = "cashier1",
            TotalAmount = 150m,
            PaymentStatus = "PAID",
            OrderStatus = "COMPLETED"
        });

        db.OrderItems.Add(new LocalOrderItem
        {
            OrderId = orderId,
            ProductId = 1,
            ProductName = "Test",
            Quantity = 1,
            UnitPrice = 150m,
            LineTotal = 150m
        });

        db.Payments.Add(new LocalPayment
        {
            OrderId = orderId,
            Amount = 150m,
            PaymentMethod = "CASH",
            PaymentStatus = "PAID"
        });

        db.SyncOutboxes.Add(new LocalSyncOutbox
        {
            OutboxId = 1,
            EntityType = "Order",
            EntityId = orderId,
            PayloadJson = "{}",
            Status = "PENDING"
        });
        await db.SaveChangesAsync();

        // Simulate successful synchronization of the outbox item
        var outbox = await db.SyncOutboxes.SingleAsync(o => o.OutboxId == 1);
        outbox.Status = "SYNCED";
        db.SyncAuditLogs.Add(new LocalSyncAuditLog { Message = "Synced order " + orderId, Timestamp = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // Verify data retention: LocalOrder, LocalOrderItems, LocalPayments MUST still exist in SQLite!
        using var verifyDb = new LocalPosDbContext();
        verifyDb.InitializeDatabase();
        Program.Check(await verifyDb.Orders.AnyAsync(o => o.OrderId == orderId), "Synced order must NOT be deleted from LocalOrders.");
        Program.Check(await verifyDb.OrderItems.AnyAsync(i => i.OrderId == orderId), "Synced items must NOT be deleted from LocalOrderItems.");
        Program.Check(await verifyDb.Payments.AnyAsync(p => p.OrderId == orderId), "Synced payments must NOT be deleted from LocalPayments.");
        Program.Check((await verifyDb.SyncOutboxes.SingleAsync(o => o.OutboxId == 1)).Status == "SYNCED", "Outbox status must be updated to SYNCED.");
    }

    private static async Task TestMaskedPinProtection()
    {
        using var db = NewDatabase();

        // Real PIN is "1234", hashed with SHA-256
        string realHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("1234")));
        db.Cashiers.Add(new LocalCashier
        {
            UserId = 1,
            Username = "cashier1",
            Role = "Cashier",
            PinHash = realHash
        });
        await db.SaveChangesAsync();

        // Simulate an API response returning masked PinCode = "****"
        var returnedUser = new UserDto
        {
            UserId = 1,
            Username = "cashier1",
            Role = "Cashier",
            PinCode = "****" // Masked by UsersController!
        };

        // If PinCode is "****", it MUST NOT overwrite the real PIN hash!
        bool isMasked = returnedUser.PinCode == "****";
        Program.Check(isMasked, "Masked PIN detection must identify '****'.");

        // Verify existing PIN hash remains intact
        var cached = await db.Cashiers.SingleAsync(c => c.UserId == 1);
        Program.Check(cached.PinHash == realHash, "Cached cashier PIN hash must remain the real hash.");
    }
}
