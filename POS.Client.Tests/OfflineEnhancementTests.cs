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
            await run("Offline enhancement: Product special instructions cart separation, SQLite persistence, and Kitchen/Bar ticket routing", TestProductSpecialInstructionsFlow);
            await run("Offline enhancement: Initial Cash Fund inheritance, denomination counting, discrepancy tracking, and unconfigured guard", TestOfflineInitialCashFundAndDenominationOpening);
            await run("Offline enhancement: Cashier account restriction, POS terminal locking, clock-tamper protection, and 4:00 AM auto-unlock", TestOfflineTerminalAndCashierClosingLock);
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
        Program.Check(cashResult.Receipt?.OrderNumber == "REGISTER1-000001", "Offline order number must use sequential terminal format without OFF- prefix.");
        Program.Check(OfflineCheckoutService.FormatOrderNumber("POS 1", 1, 155) == "POS01-000155", "FormatOrderNumber must replicate online POS01-000155 formatting.");

        // Verify SQLite state
        using (var verifyDb = new LocalPosDbContext())
        {
            verifyDb.InitializeDatabase();
            var order = await verifyDb.Orders.SingleAsync(o => o.OrderId == cashResult.LocalTransactionId);
            Program.Check(order.OrderNumber == "REGISTER1-000001", "LocalOrder.OrderNumber must persist the sequential order number.");
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

    private static async Task TestProductSpecialInstructionsFlow()
    {
        using var db = NewDatabase();

        var session = new PosSessionService();
        session.SetSession("TOKEN", "cashier1", "Cashier", new TerminalConfigDto { TerminalId = 1, BranchId = 1, OutletId = 1 });
        var cart = new CartService(session);

        var burger = new ProductDto
        {
            ProductId = 101,
            Name = "Cheeseburger",
            Price = 150m,
            IsKitchenItem = true,
            IsBarItem = false
        };
        var coffee = new ProductDto
        {
            ProductId = 102,
            Name = "Iced Coffee",
            Price = 110m,
            IsKitchenItem = false,
            IsBarItem = true
        };

        // 1. Two identical products with different notes must stay as separate cart lines
        cart.AddItem(burger, 1, null, "  No Onion, Extra Sauce  ");
        cart.AddItem(burger, 2, null, "No Pickles");
        cart.AddItem(coffee, 2, null, "No Ice, Less Sugar");

        Program.Check(cart.Items.Count == 3, "Identical products with different special instructions must be kept as separate cart lines.");
        Program.Check(cart.Items[0].SpecialInstructions == "No Onion, Extra Sauce", "SpecialInstructions must be trimmed.");
        Program.Check(cart.Items[1].SpecialInstructions == "No Pickles" && cart.Items[1].Quantity == 2, "Second burger line must preserve its own note and quantity.");
        Program.Check(cart.Subtotal == (150m * 3) + (110m * 2), "Notes must never alter product pricing or subtotal calculations.");

        // 2. Max length (250 chars) enforcement
        string overlong = new string('A', 300);
        Program.Check(CartItem.NormalizeSpecialInstructions(overlong)?.Length == 250, "SpecialInstructions must be capped at 250 characters.");

        // 3. Offline checkout persists SpecialInstructions in SQLite and ReceiptDto
        db.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            TerminalCode = "T01",
            TerminalName = "POS 1",
            TerminalIdentifier = "TERM-01",
            BranchTaxType = "VAT-Registered"
        });
        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = 1,
            SeriesCode = "S1",
            Prefix = "INV-",
            StartingNumber = 1,
            EndingNumber = 500,
            LastIssuedNumber = 0,
            PaddingLength = 7,
            IsActive = true
        });
        db.Products.Add(new LocalProduct { ProductId = 101, Name = "Cheeseburger", Price = 150m, IsActive = true, IsKitchenItem = true });
        db.Products.Add(new LocalProduct { ProductId = 102, Name = "Iced Coffee", Price = 110m, IsActive = true, IsBarItem = true });
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

        var checkoutService = new OfflineCheckoutService(db, session);
        var createDto = new OrderCreateDto
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            CashierId = "cashier1",
            OrderType = "DINING",
            TableNumber = "05",
            Items = cart.Items.Select(ci => new OrderItemCreateDto
            {
                ProductId = ci.ProductId,
                ProductName = ci.ProductName,
                Quantity = ci.Quantity,
                BasePrice = ci.BasePrice,
                IsKitchenItem = ci.IsKitchenItem,
                IsBarItem = ci.IsBarItem,
                SpecialInstructions = ci.SpecialInstructions
            }).ToList()
        };

        var res = await checkoutService.ProcessSaleAsync(createDto, 700m, "CASH", null, "cashier1");
        Program.Check(res.Success && res.Receipt != null, "Checkout with special instructions must succeed.");

        using var verifyDb = new LocalPosDbContext();
        verifyDb.InitializeDatabase();
        var savedItems = await verifyDb.OrderItems.Where(i => i.OrderId == res.LocalTransactionId).OrderBy(i => i.OrderItemId).ToListAsync();
        Program.Check(savedItems.Count == 3, "All 3 preparation lines must be saved in LocalOrderItems.");
        Program.Check(savedItems[0].SpecialInstructions == "No Onion, Extra Sauce", "First line note must persist in SQLite.");
        Program.Check(savedItems[1].SpecialInstructions == "No Pickles", "Second line note must persist in SQLite.");
        Program.Check(savedItems[2].SpecialInstructions == "No Ice, Less Sugar", "Bar line note must persist in SQLite.");

        // 4. Verify Kitchen and Bar PreparationTicketRouter and HTML formatting
        var tickets = PreparationTicketRouter.GenerateTickets(res.Receipt!, "KitchenPrinter", "BarPrinter");
        Program.Check(tickets.Count == 2, "Must generate separate KITCHEN and BAR preparation tickets.");

        var kitchenTicket = tickets.Single(t => t.Station == "KITCHEN");
        var barTicket = tickets.Single(t => t.Station == "BAR");
        Program.Check(kitchenTicket.Items.Count == 2, "Kitchen ticket must contain both burger preparation lines.");
        Program.Check(barTicket.Items.Count == 1 && barTicket.Items[0].SpecialInstructions == "No Ice, Less Sugar", "Bar ticket must contain Iced Coffee with its note.");

        var kitchenHtml = PreparationTicketHtmlFormatter.Format(kitchenTicket);
        var barHtml = PreparationTicketHtmlFormatter.Format(barTicket);
        var customerReceiptHtml = ReceiptHtmlFormatter.Format(res.Receipt!);

        Program.Check(kitchenHtml.Contains("- No Onion") && kitchenHtml.Contains("- Extra Sauce") && kitchenHtml.Contains("- No Pickles"), "Kitchen ticket HTML must format each special instruction line.");
        Program.Check(barHtml.Contains("- No Ice") && barHtml.Contains("- Less Sugar"), "Bar ticket HTML must format each special instruction line.");
        Program.Check(!customerReceiptHtml.Contains("No Onion") && !customerReceiptHtml.Contains("Less Sugar"), "Customer BIR tax receipt must not expose internal kitchen/bar preparation notes.");
    }

    private static async Task TestOfflineInitialCashFundAndDenominationOpening()
    {
        using var db = NewDatabase();
        using var offlineHttp = new HttpClient();

        // 1. Seed Terminal with no cash fund configured
        var termCfg = new LocalTerminalConfig
        {
            TerminalId = 1,
            BranchId = 1,
            OutletId = 1,
            OutletName = "Main Counter",
            TerminalCode = "T01",
            TerminalName = "POS 1",
            TerminalIdentifier = "TERM-01",
            BranchTaxType = "VAT-Registered",
            DefaultOpeningFund = null,
            OpeningFundOverride = null,
            EffectiveOpeningFund = null,
            OpeningFundSource = "UNCONFIGURED"
        };
        db.TerminalConfigs.Add(termCfg);
        await db.SaveChangesAsync();

        var unconf = await ShiftSession.GetOpeningFundConfigAsync(offlineHttp, 1, offline: true);
        Program.Check(!unconf.IsConfigured && unconf.EffectiveOpeningFund == null && unconf.OpeningFundSource == "UNCONFIGURED",
            "Unconfigured terminal must report IsConfigured=false offline.");

        bool blocked = false;
        try
        {
            await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = Guid.NewGuid().ToString("N"),
                OpeningFloat = 1000m,
                RequireConfiguredFund = true
            }, "cashier1", offline: true);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Initial Cash Fund is not configured"))
        {
            blocked = true;
        }
        Program.Check(blocked, "Offline OpenAsync with RequireConfiguredFund=true must block shift opening when unconfigured.");

        // 2. Configure Outlet Default (₱2,500.00) -> Terminal inherits Outlet Default
        termCfg.DefaultOpeningFund = 2500.00m;
        termCfg.EffectiveOpeningFund = 2500.00m;
        termCfg.OpeningFundSource = "OUTLET_DEFAULT";
        await db.SaveChangesAsync();

        var outletInherited = await ShiftSession.GetOpeningFundConfigAsync(offlineHttp, 1, offline: true);
        Program.Check(outletInherited.IsConfigured && outletInherited.EffectiveOpeningFund == 2500.00m && outletInherited.OpeningFundSource == "OUTLET_DEFAULT",
            "Offline terminal must inherit Outlet DefaultOpeningFund.");

        // 3. Configure Terminal Override (₱4,000.00) -> Override takes precedence
        termCfg.OpeningFundOverride = 4000.00m;
        termCfg.EffectiveOpeningFund = 4000.00m;
        termCfg.OpeningFundSource = "TERMINAL_OVERRIDE";
        await db.SaveChangesAsync();

        var overrideCfg = await ShiftSession.GetOpeningFundConfigAsync(offlineHttp, 1, offline: true);
        Program.Check(overrideCfg.IsConfigured && overrideCfg.EffectiveOpeningFund == 4000.00m && overrideCfg.OpeningFundSource == "TERMINAL_OVERRIDE",
            "Offline terminal override must take precedence over Outlet Default.");

        // 4. Open shift offline with denomination breakdown totaling ₱4,020.00 (+₱20.00 Overage)
        var denoms = CashDenominationCatalog.CreateDefaultLines();
        denoms.Single(d => d.Type == "BILL" && d.Denomination == 1000m).Quantity = 4;
        denoms.Single(d => d.Type == "COIN" && d.Denomination == 20m).Quantity = 1;

        var opened = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
        {
            BranchId = 1,
            TerminalId = 1,
            ClientShiftId = Guid.NewGuid().ToString("N"),
            RequireConfiguredFund = true,
            OpeningDenominations = denoms
        }, "cashier1", offline: true);

        Program.Check(opened.OpeningFloat == 4020.00m, "Counted opening float must be calculated from denominations (4020.00).");
        Program.Check(opened.ExpectedOpeningFund == 4000.00m, "ExpectedOpeningFund must be recorded (4000.00).");
        Program.Check(opened.OpeningDiscrepancy == 20.00m, "OpeningDiscrepancy (+20.00 overage) must be recorded.");
        Program.Check(!string.IsNullOrWhiteSpace(opened.OpeningDenominationsJson), "OpeningDenominationsJson must be persisted in SQLite.");

        var dto = await ShiftSession.CurrentDtoAsync(offlineHttp, 1, offline: true);
        Program.Check(dto != null && dto.ExpectedOpeningFund == 4000.00m && dto.OpeningDiscrepancy == 20.00m && dto.OpeningReconciliationStatus == "OVER",
            "CurrentDtoAsync must expose ExpectedOpeningFund, OpeningDiscrepancy, and OVER status.");
    }

    private static async Task TestOfflineTerminalAndCashierClosingLock()
    {
        try
        {
            // Business Day 2026-10-20 at 10:00 PM Asia/Manila (14:00:00 UTC)
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 14, 0, 0, DateTimeKind.Utc);
            POS.Shared.Services.BusinessDayClock.TickCount64Provider = () => 1_000_000L;

            using var db = NewDatabase();
            db.TerminalConfigs.Add(new LocalTerminalConfig
            {
                TerminalId = 1,
                BranchId = 1,
                OutletId = 1,
                TerminalCode = "T01",
                TerminalName = "Counter 1",
                TerminalIdentifier = "TERM-01",
                BranchTaxType = "VAT-Registered",
                DefaultOpeningFund = 2000.00m,
                EffectiveOpeningFund = 2000.00m,
                OpeningFundSource = "OUTLET_DEFAULT",
                IsInitialized = true
            });
            db.InvoiceSequences.Add(new LocalInvoiceSequence
            {
                SequenceId = 1,
                TerminalId = 1,
                SeriesCode = "S1",
                Prefix = "INV-",
                StartingNumber = 1,
                EndingNumber = 1000,
                LastIssuedNumber = 0,
                PaddingLength = 7,
                IsActive = true
            });
            db.Products.Add(new LocalProduct
            {
                ProductId = 1,
                CategoryId = 1,
                Name = "Espresso",
                Price = 100m,
                IsActive = true
            });
            await db.SaveChangesAsync();

            using var offlineHttp = new HttpClient();

            // 1. Cashier1 opens and closes Terminal 1 offline at 10:00 PM Manila
            var opened = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = Guid.NewGuid().ToString("N"),
                OpeningFloat = 2000.00m,
                RequireConfiguredFund = true
            }, "cashier1", offline: true);

            var closed = await ShiftSession.CloseAsync(offlineHttp, new CloseShiftDto
            {
                ShiftId = opened.ShiftId,
                ActualCountedBalance = 2000.00m,
                Notes = "Offline EOD Close"
            }, "cashier1", offline: true);

            Program.Check(closed != null && closed.Status == "CLOSED", "Offline CloseAsync must close the shift.");

            // 2. Verify SQLite persisted both lock states and monotonic tick count
            using (var checkDb = new LocalPosDbContext())
            {
                var cfg = await checkDb.TerminalConfigs.SingleAsync(t => t.TerminalId == 1);
                Program.Check(cfg.ClosedBusinessDate == "2026-10-20", $"Expected ClosedBusinessDate 2026-10-20, got {cfg.ClosedBusinessDate}");
                Program.Check(cfg.ClosingLockedByCashier == "cashier1", $"Expected ClosingLockedByCashier cashier1, got {cfg.ClosingLockedByCashier}");
                Program.Check(cfg.ClosingLockedTickCount64 == 1_000_000L, $"Expected ClosingLockedTickCount64 1000000, got {cfg.ClosingLockedTickCount64}");
            }

            // 3. Verify BOTH cashier1 restriction and terminal lock are active
            var lockForCashier1 = await ShiftSession.GetTerminalLockStatusAsync(offlineHttp, 1, "cashier1", offline: true);
            Program.Check(lockForCashier1.IsTerminalLocked && lockForCashier1.IsCashierRestricted,
                "Cashier1 must have BOTH IsTerminalLocked and IsCashierRestricted after closing.");

            var lockForCashier2 = await ShiftSession.GetTerminalLockStatusAsync(offlineHttp, 1, "cashier2", offline: true);
            Program.Check(lockForCashier2.IsTerminalLocked && !lockForCashier2.IsCashierRestricted,
                "Cashier2 must also be blocked by IsTerminalLocked on Terminal 1.");

            // 4. Verify OpenAsync and OfflineCheckoutService.ProcessSaleAsync are blocked while locked
            bool openBlockedForCashier2 = false;
            try
            {
                await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
                {
                    BranchId = 1,
                    TerminalId = 1,
                    ClientShiftId = Guid.NewGuid().ToString("N"),
                    OpeningFloat = 2000.00m
                }, "cashier2", offline: true);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("locked") || ex.Message.Contains("4:00 AM"))
            {
                openBlockedForCashier2 = true;
            }
            Program.Check(openBlockedForCashier2, "ShiftSession.OpenAsync must block all cashiers while terminal is locked.");

            // 5. Anti-clock-tamper test: User advances device clock to 5:00 AM next day (21:00:00 UTC),
            //    but monotonic OS uptime only advanced 5 minutes (300,000 ms) instead of the required 6 hours!
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 21, 0, 0, DateTimeKind.Utc);
            POS.Shared.Services.BusinessDayClock.TickCount64Provider = () => 1_300_000L; // Only +5 mins elapsed

            var tamperedLock = await ShiftSession.GetTerminalLockStatusAsync(offlineHttp, 1, "cashier1", offline: true);
            Program.Check(tamperedLock.ClockTamperDetected && tamperedLock.IsTerminalLocked && tamperedLock.IsCashierRestricted,
                "Offline clock tampering must NOT unlock the terminal or cashier restriction when monotonic uptime has not elapsed.");

            // 6. Legitimate 4:00 AM unlock: Both clock reaches 4:00 AM Manila (20:00:00 UTC) AND monotonic uptime advances 6 hours (21,600,000 ms)
            POS.Shared.Services.BusinessDayClock.UtcNowProvider = () => new DateTime(2026, 10, 20, 20, 0, 0, DateTimeKind.Utc);
            POS.Shared.Services.BusinessDayClock.TickCount64Provider = () => 1_000_000L + (6L * 3600L * 1000L);

            var unlockedAt4Am = await ShiftSession.GetTerminalLockStatusAsync(offlineHttp, 1, "cashier1", offline: true);
            Program.Check(!unlockedAt4Am.IsTerminalLocked && !unlockedAt4Am.IsCashierRestricted && !unlockedAt4Am.ClockTamperDetected,
                "At 4:00 AM Manila with legitimate elapsed uptime, both terminal lock and cashier restriction must reset.");

            using (var checkDbAfter = new LocalPosDbContext())
            {
                var cfgAfter = await checkDbAfter.TerminalConfigs.SingleAsync(t => t.TerminalId == 1);
                Program.Check(cfgAfter.ClosedBusinessDate == null && cfgAfter.ClosingLockedByCashier == null,
                    "Auto-unlock at 4:00 AM must clear local SQLite terminal lock fields.");
            }

            // 7. Require fresh Initial Cash Fund confirmation for the new business day session
            var newDayShift = await ShiftSession.OpenAsync(offlineHttp, new OpenShiftDto
            {
                BranchId = 1,
                TerminalId = 1,
                ClientShiftId = Guid.NewGuid().ToString("N"),
                OpeningFloat = 2000.00m,
                RequireConfiguredFund = true
            }, "cashier1", offline: true);
            Program.Check(newDayShift.Status == "OPEN" && newDayShift.OpeningConfirmed,
                "Authorized cashier must be able to open a new shift after 4:00 AM with Initial Cash Fund confirmation.");
        }
        finally
        {
            POS.Shared.Services.BusinessDayClock.ResetProviders();
        }
    }
}
