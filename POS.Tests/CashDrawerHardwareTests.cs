using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private sealed class LoopbackPrinterServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;
        public List<byte[]> ReceivedPayloads { get; } = new();
        public int Port { get; }

        public LoopbackPrinterServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _acceptLoop = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    using (client)
                    using (var stream = client.GetStream())
                    using (var ms = new MemoryStream())
                    {
                        var buffer = new byte[1024];
                        int read;
                        while ((read = await stream.ReadAsync(buffer, _cts.Token)) > 0)
                        {
                            ms.Write(buffer, 0, read);
                        }
                        if (ms.Length > 0)
                        {
                            lock (ReceivedPayloads)
                            {
                                ReceivedPayloads.Add(ms.ToArray());
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    client?.Dispose();
                    break;
                }
                catch
                {
                    client?.Dispose();
                }
            }
        }

        public int Count
        {
            get { lock (ReceivedPayloads) return ReceivedPayloads.Count; }
        }

        public byte[]? LastPayload
        {
            get { lock (ReceivedPayloads) return ReceivedPayloads.LastOrDefault(); }
        }

        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _cts.Cancel();
            _listener.Stop();
            try { await _acceptLoop; } catch { }
            _cts.Dispose();
        }
    }

    private static (string TempDbPath, object? PrevPath, LocalPosDbContext Db, PosSessionService Session) CreateOfflineDrawerTestEnv(
        string username = "cashier1",
        string role = "Cashier")
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"pos_drawer_test_{Guid.NewGuid():N}.db");
        object? prevPath = AppContext.GetData("POS.LocalDatabasePath");
        AppContext.SetData("POS.LocalDatabasePath", tempDbPath);
        EscPosPrinterService.ClearInMemoryClaimsForTests();

        var db = new LocalPosDbContext();
        db.InitializeDatabase();

        db.Branches.Add(new LocalBranch
        {
            BranchId = 1,
            Name = "Main Branch",
            Code = "BR-01",
            TaxType = "VAT-Registered",
            VatDisplay = true,
            IsActive = true,
            UpdatedAt = DateTime.UtcNow
        });
        db.TerminalConfigs.Add(new LocalTerminalConfig
        {
            TerminalId = 1,
            OutletId = 1,
            OutletName = "Counter 1",
            TerminalCode = "POS-1",
            TerminalName = "POS1",
            TerminalIdentifier = "DEV-DRAWER-TEST",
            DeviceUuid = "DEV-DRAWER-TEST",
            BranchId = 1,
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            BranchTaxType = "VAT-Registered",
            VatDisplay = true,
            IsActivated = true,
            IsInitialized = true,
            AllowSync = true
        });
        db.Products.Add(new LocalProduct
        {
            ProductId = 1,
            CategoryId = 1,
            CategoryName = "Food",
            Name = "Classic Burger",
            Price = 100m,
            TaxName = "VATable",
            IsActive = true
        });
        db.InvoiceSequences.Add(new LocalInvoiceSequence
        {
            SequenceId = 1,
            TerminalId = 1,
            SeriesCode = "TXN1",
            Prefix = "TXN1-",
            StartingNumber = 1,
            EndingNumber = 1000,
            LastIssuedNumber = 0,
            PaddingLength = 6,
            IsActive = true
        });
        db.CashDrawerShifts.Add(new LocalCashDrawerShift
        {
            ShiftId = 101,
            ClientShiftId = "shift-drawer-1",
            BranchId = 1,
            TerminalId = 1,
            CashierId = username,
            OpeningFloat = 1000m,
            ExpectedBalance = 1000m,
            OpeningConfirmed = true,
            Status = "OPEN",
            OpenedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        db.Cashiers.AddRange(
            new LocalCashier
            {
                UserId = 1,
                Username = "cashier1",
                Role = "Cashier",
                PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("1111")))
            },
            new LocalCashier
            {
                UserId = 2,
                Username = "manager1",
                Role = "Manager",
                PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("9999")))
            },
            new LocalCashier
            {
                UserId = 3,
                Username = "senior1",
                Role = "SeniorCashier",
                PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("8888")))
            });
        db.SaveChanges();

        var session = new PosSessionService();
        session.SetSession("TERMINAL-JWT", username, role, new TerminalConfigDto
        {
            TerminalId = 1,
            OutletId = 1,
            BranchId = 1,
            BranchName = "Main Branch",
            BranchCode = "BR-01",
            TerminalName = "POS1"
        });

        return (tempDbPath, prevPath, db, session);
    }

    private static OrderCreateDto CreateSampleOrderDto(string cashier, List<PaymentSplitDto>? splits = null) => new()
    {
        BranchId = 1,
        OutletId = 1,
        TerminalId = 1,
        ShiftId = 101,
        ClientShiftId = "shift-drawer-1",
        CashierId = cashier,
        OrderType = "DINING",
        Splits = splits,
        Items = new()
        {
            new OrderItemCreateDto
            {
                ProductId = 1,
                ProductName = "Classic Burger",
                Quantity = 1m,
                BasePrice = 100m,
                TaxName = "VATable"
            }
        }
    };

    private static EscPosPrinterService CreateUnifiedService(
        IEscPosNetworkTransport transport,
        PosSessionService session,
        IUsbPrinterTransport? usbTransport = null)
    {
        usbTransport ??= new UnsupportedUsbPrinterTransport();
        return new EscPosPrinterService(
            new EthernetPrinterConnection(transport),
            new UsbPrinterConnection(usbTransport),
            usbTransport,
            session,
            new BasicEscPosImageRasterizer());
    }

    private static async Task EnableLoopbackPrinterConfigAsync(
        ICashDrawerHardwareService hw,
        PosSessionService session,
        int port,
        int kickPin = 0,
        int pulseOn = 25,
        int pulseOff = 250)
    {
        var pin = ShiftSession.IsSupervisorRole(session.Role) ? null : "9999";
        var saveRes = await hw.SaveConfigAsync(new LocalPrinterHardwareConfig
        {
            TerminalId = 1,
            CashDrawerEnabled = true,
            PrinterIpAddress = "127.0.0.1",
            PrinterPort = port,
            ConnectTimeoutMs = 2000,
            KickPin = kickPin,
            PulseOnUnits = pulseOn,
            PulseOffUnits = pulseOff
        }, pin);
        Assert(saveRes.Success, $"Expected loopback printer config save to succeed, got: {saveRes.Message}");
    }

    private static Task TestCashDrawer01_EscPosCommandsAndValidation()
    {
        byte[] pin2Default = EscPosCommands.BuildCashDrawerKick();
        Assert(pin2Default.SequenceEqual(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }),
            $"Default XP-80T command must be 1B 70 00 19 FA, got {EscPosCommands.FormatHex(pin2Default)}");
        Assert(EscPosCommands.FormatHex(pin2Default) == "1B 70 00 19 FA", "Command hex formatting mismatch.");

        byte[] pin5Custom = EscPosCommands.BuildCashDrawerKick(1, 30, 200);
        Assert(pin5Custom.SequenceEqual(new byte[] { 0x1B, 0x70, 0x01, 0x1E, 0xC8 }),
            "Pin 5 custom pulse command mismatch.");

        bool threwInvalidPin = false;
        try { _ = EscPosCommands.BuildCashDrawerKick(2); } catch (ArgumentOutOfRangeException) { threwInvalidPin = true; }
        Assert(threwInvalidPin, "Invalid drawer kick pin must throw.");

        Assert(EscPosCommands.TryValidateEndpoint(" 192.168.1.100 ", 9100, out var normIp, out _) && normIp == "192.168.1.100",
            "Valid IPv4 endpoint must normalize cleanly.");
        Assert(!EscPosCommands.TryValidateEndpoint("http://192.168.1.100", 9100, out _, out _),
            "URI scheme must be rejected for raw printer IP.");
        Assert(!EscPosCommands.TryValidateEndpoint("192.168.1.100", 0, out _, out _),
            "TCP port 0 must be rejected.");

        byte[] testTicket = EscPosCommands.BuildPrinterTestTicket("POS1", "192.168.1.100:9100", new DateTime(2026, 11, 15, 12, 0, 0));
        Assert(testTicket[0] == 0x1B && testTicket[1] == 0x40, "Printer test ticket must begin with ESC @ init.");
        Assert(Encoding.ASCII.GetString(testTicket).Contains("XPRINTER XP-80T CONNECTION TEST", StringComparison.Ordinal),
            "Printer test ticket must include XP-80T header text.");

        return Task.CompletedTask;
    }

    private static async Task TestCashDrawer02_AutomaticCashVsNonCashAndFailed()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var hw = CreateUnifiedService(transport, session);
            await EnableLoopbackPrinterConfigAsync(hw, session, printer.Port);

            var checkout = new OfflineCheckoutService(localDb, session);

            // 1. Successful offline CASH sale -> opens drawer with 1B 70 00 19 FA
            var cashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CASH", null, "cashier1");
            Assert(cashSale.Success && cashSale.CashAmountPaid == 100m, "Cash sale must commit cleanly.");
            var cashDrawerRes = await hw.OpenForCompletedSaleAsync(cashSale);
            Assert(cashDrawerRes.Success && !cashDrawerRes.Skipped, $"Cash sale must open drawer: {cashDrawerRes.Message}");
            Assert(printer.Count == 1, $"Expected 1 drawer kick after cash sale, got {printer.Count}");
            Assert(printer.LastPayload!.SequenceEqual(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }),
                "Sent ESC/POS drawer kick payload must equal 1B 70 00 19 FA.");

            // 2. Card-only sale -> does NOT open drawer
            var cardSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "AUTH-123", "cashier1");
            Assert(cardSale.Success && cardSale.CashAmountPaid == 0m, "Card sale must commit cleanly.");
            var cardDrawerRes = await hw.OpenForCompletedSaleAsync(cardSale);
            Assert(cardDrawerRes.Skipped && printer.Count == 1, "Card-only sale must NOT open the cash drawer.");

            // 3. E-wallet (GCASH) sale -> does NOT open drawer
            var gcashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "GCASH", "GC-999", "cashier1");
            Assert(gcashSale.Success, "GCash sale must commit cleanly.");
            var gcashDrawerRes = await hw.OpenForCompletedSaleAsync(gcashSale);
            Assert(gcashDrawerRes.Skipped && printer.Count == 1, "E-wallet sale must NOT open the cash drawer.");

            // 4. Failed / insufficient cash / pending -> does NOT open drawer
            var failedCash = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 10m, "CASH", null, "cashier1");
            Assert(!failedCash.Success, "Insufficient cash must fail.");
            var failedRes = await hw.OpenForCompletedSaleAsync(failedCash);
            Assert(failedRes.Skipped && printer.Count == 1, "Failed sale must NOT open the cash drawer.");

            var syntheticPending = new OfflineCheckoutResultDto
            {
                Success = true,
                LocalTransactionId = Guid.NewGuid().ToString(),
                Receipt = new ReceiptDto
                {
                    TerminalId = 1,
                    InvoiceNumber = "PENDING-TEST",
                    Payments = new() { new PaymentResponseDto { PaymentMethod = "CASH", Amount = 100m, PaymentStatus = PaymentStatuses.Pending } }
                }
            };
            var pendingRes = await hw.OpenForCompletedSaleAsync(syntheticPending);
            Assert(pendingRes.Skipped && printer.Count == 1, "Pending payment status must NOT open the cash drawer.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestCashDrawer03_SplitPaymentRules()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var hw = CreateUnifiedService(transport, session);
            await EnableLoopbackPrinterConfigAsync(hw, session, printer.Port);
            var checkout = new OfflineCheckoutService(localDb, session);

            // 1. Split: GCASH (60) + CASH (40) -> opens ONCE
            var mixedSplit = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1", new()
            {
                new PaymentSplitDto { PaymentMethod = "GCASH", Amount = 60m, AmountTendered = 60m, ReferenceNumber = "GC1" },
                new PaymentSplitDto { PaymentMethod = "CASH", Amount = 40m, AmountTendered = 50m }
            }), 50m, "CASH", null, "cashier1");
            Assert(mixedSplit.Success && mixedSplit.CashAmountPaid == 40m, "Mixed split must commit.");
            var r1 = await hw.OpenForCompletedSaleAsync(mixedSplit);
            Assert(r1.Success && printer.Count == 1, "Split payment with cash component must open drawer once.");

            // 2. Split: CASH (50) + CASH (50) -> opens ONCE (never twice for multiple cash payment lines)
            var multiCashSplit = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1", new()
            {
                new PaymentSplitDto { PaymentMethod = "CASH", Amount = 50m, AmountTendered = 50m },
                new PaymentSplitDto { PaymentMethod = "CASH", Amount = 50m, AmountTendered = 50m }
            }), 100m, "CASH", null, "cashier1");
            Assert(multiCashSplit.Success, "Multi-cash split must commit.");
            var r2 = await hw.OpenForCompletedSaleAsync(multiCashSplit);
            Assert(r2.Success && printer.Count == 2, "Two cash split lines within one transaction must produce only 1 drawer pulse.");

            // 3. Split: CARD (50) + GCASH (50) -> stays closed
            var nonCashSplit = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1", new()
            {
                new PaymentSplitDto { PaymentMethod = "CARD", Amount = 50m, AmountTendered = 50m, ReferenceNumber = "C1" },
                new PaymentSplitDto { PaymentMethod = "GCASH", Amount = 50m, AmountTendered = 50m, ReferenceNumber = "G1" }
            }), 100m, "CARD", "C1", "cashier1");
            Assert(nonCashSplit.Success && nonCashSplit.CashAmountPaid == 0m, "Card+GCash split must commit.");
            var r3 = await hw.OpenForCompletedSaleAsync(nonCashSplit);
            Assert(r3.Skipped && printer.Count == 2, "Card+GCash split with zero cash must NOT open cash drawer.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestCashDrawer04_DuplicateReprintAndRestartProtection()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var hw = CreateUnifiedService(transport, session);
            await EnableLoopbackPrinterConfigAsync(hw, session, printer.Port);
            var checkout = new OfflineCheckoutService(localDb, session);

            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CASH", null, "cashier1");
            Assert(sale.Success, "Sale must commit.");

            // 1. Concurrent / repeated callbacks for the exact same finalized transaction
            var t1 = hw.OpenForCompletedSaleAsync(sale);
            var t2 = hw.OpenForCompletedSaleAsync(sale);
            var results = await Task.WhenAll(t1, t2);
            Assert(printer.Count == 1, $"Concurrent callbacks must trigger only 1 drawer kick, got {printer.Count}");
            Assert(results.Any(r => r.AlreadyOpened), "Duplicate callback must report AlreadyOpened.");

            // 2. Simulated application restart: clear RAM claims & construct brand new service instance
            EscPosPrinterService.ClearInMemoryClaimsForTests();
            var restartedHw = CreateUnifiedService(transport, session);
            var afterRestart = await restartedHw.OpenForCompletedSaleAsync(sale);
            Assert(afterRestart.Skipped && afterRestart.AlreadyOpened && printer.Count == 1,
                "Durable SQLite claim must block duplicate drawer opening even after application restart.");

            // 3. Receipt reprint must never open drawer even with brand new TransactionId
            var reprintDto = new OfflineCheckoutResultDto
            {
                Success = true,
                LocalTransactionId = Guid.NewGuid().ToString(),
                Receipt = new ReceiptDto
                {
                    TerminalId = 1,
                    InvoiceNumber = sale.InvoiceNumber,
                    IsReprint = true,
                    Payments = sale.Receipt!.Payments
                }
            };
            var reprintRes = await restartedHw.OpenForCompletedSaleAsync(reprintDto);
            Assert(reprintRes.Skipped && printer.Count == 1, "Reprint receipt must never trigger cash drawer.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestCashDrawer05_ManualOpenAuthorizationAndAudit()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("cashier1", "Cashier");
        try
        {
            var hw = CreateUnifiedService(transport, session);
            await EnableLoopbackPrinterConfigAsync(hw, session, printer.Port);

            // 1. Standard Cashier without supervisor PIN -> rejected & audited
            var deniedNoPin = await hw.ManualOpenAsync("Customer change", null);
            Assert(!deniedNoPin.Success && deniedNoPin.Unauthorized && printer.Count == 0,
                "Standard Cashier must not open drawer manually without supervisor PIN.");

            // 2. Standard Cashier using another Cashier's PIN -> rejected
            var deniedWrongRole = await hw.ManualOpenAsync("Customer change", "1111");
            Assert(!deniedWrongRole.Success && deniedWrongRole.Unauthorized && printer.Count == 0,
                "Non-supervisor PIN must not authorize manual drawer open.");

            // 3. Valid Supervisor PIN but missing/short reason -> rejected
            var deniedReason = await hw.ManualOpenAsync(" ", "9999");
            Assert(!deniedReason.Success && printer.Count == 0, "Empty reason must be rejected.");

            // 4. Offline Cashier with valid Manager PIN ("9999") + meaningful reason -> allowed & audited
            var allowedOffline = await hw.ManualOpenAsync("Change bills for customer", "9999");
            Assert(allowedOffline.Success && printer.Count == 1, $"Valid supervisor PIN must open drawer offline: {allowedOffline.Message}");

            // 5. Manager logged in directly -> allowed with own role without PIN
            var mgrSession = new PosSessionService();
            mgrSession.SetSession("MGR-JWT", "manager1", "Manager", new TerminalConfigDto { TerminalId = 1, OutletId = 1, BranchId = 1, TerminalName = "POS1" });
            var mgrHw = CreateUnifiedService(transport, mgrSession);
            var mgrOpen = await mgrHw.ManualOpenAsync("Mid-shift float verification", null);
            Assert(mgrOpen.Success && printer.Count == 2, $"Manager must open drawer directly: {mgrOpen.Message}");

            var auditLogs = await hw.GetRecentEventsAsync(1, 20);
            Assert(auditLogs.Any(e => e.ActionType == "MANUAL_OPEN" && e.Status == "OPENED" && e.RequestedBy == "cashier1" && e.AuthorizedBy == "manager1" && e.Reason == "Change bills for customer"),
                "Manual open audit entry must capture requester, approver, reason, and OPENED status.");
            Assert(auditLogs.Any(e => e.ActionType == "MANUAL_OPEN" && e.Status == "UNAUTHORIZED" && e.RequestedBy == "cashier1"),
                "Unauthorized manual open attempt must be audited.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestCashDrawer06_PrinterUnavailableResilienceAndHardwareTests()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("manager1", "Manager");
        try
        {
            var hw = CreateUnifiedService(transport, session);

            // 1. Configure reachable port first for Pin 5 test & TestPrinter / TestCashDrawer
            await EnableLoopbackPrinterConfigAsync(hw, session, printer.Port, kickPin: 1, pulseOn: 25, pulseOff: 250);

            var testPrinter = await hw.TestPrinterAsync();
            Assert(testPrinter.Success && printer.Count == 1, $"TestPrinter must succeed: {testPrinter.Message}");
            Assert(Encoding.ASCII.GetString(printer.LastPayload!).Contains("XPRINTER XP-80T CONNECTION TEST", StringComparison.Ordinal),
                "TestPrinter must send formatted ESC/POS slip.");

            var testDrawer = await hw.TestCashDrawerAsync();
            Assert(testDrawer.Success && printer.Count == 2, $"TestCashDrawer must succeed: {testDrawer.Message}");
            Assert(printer.LastPayload!.SequenceEqual(new byte[] { 0x1B, 0x70, 0x01, 0x19, 0xFA }),
                "TestCashDrawer must honor configured Pin 5 setting.");

            // 2. Stop/pick unused port to simulate powered-off printer after saving local cash sale
            int closedPort = printer.Port;
            await printer.DisposeAsync();

            var checkout = new OfflineCheckoutService(localDb, session);
            var cashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("manager1"), 200m, "CASH", null, "manager1");
            Assert(cashSale.Success, "Cash sale must succeed even when printer is unreachable.");

            string? receivedWarning = null;
            hw.OnCashierWarning += msg => receivedWarning = msg;

            var drawerRes = await hw.OpenForCompletedSaleAsync(cashSale);
            Assert(!drawerRes.Success && !string.IsNullOrWhiteSpace(receivedWarning),
                "Unreachable printer must return failure result and notify cashier warning handler.");

            // 3. Verify saved LocalOrder, LocalPayment, and LocalCashDrawerShift remain completely intact!
            using var verifyDb = new LocalPosDbContext();
            var savedOrder = await verifyDb.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.OrderId == cashSale.LocalTransactionId);
            var savedShift = await verifyDb.CashDrawerShifts.AsNoTracking().SingleAsync(s => s.ShiftId == 101);
            Assert(savedOrder != null && savedOrder.PaymentStatus == PaymentStatuses.Paid,
                "Saved cash order must remain PAID when cash drawer hardware fails.");
            Assert(savedShift.TotalCashSales == 100m && savedShift.ExpectedBalance == 1100m,
                "Shift totals must remain accurate even when cash drawer hardware fails.");
            Assert(await verifyDb.CashDrawerHardwareEvents.AnyAsync(e => e.TransactionId == cashSale.LocalTransactionId && e.Status == "FAILED"),
                "Hardware failure must be logged cleanly with FAILED status.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }
}
