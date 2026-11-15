using System.Text;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private static async Task WaitForPayloadsAsync(LoopbackPrinterServer server, int expectedCount, int timeoutMs = 1500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (server.Count < expectedCount && DateTime.UtcNow < deadline)
        {
            await Task.Delay(15);
        }
        Assert(server.Count == expectedCount, $"Expected {expectedCount} TCP payload(s), but server received {server.Count}.");
    }

    private static bool ContainsDrawerKickSequence(byte[] payload)
    {
        for (int i = 0; i <= payload.Length - 2; i++)
        {
            if (payload[i] == 0x1B && payload[i + 1] == 0x70)
                return true;
        }
        return false;
    }

    private static async Task ConfigureUnifiedAutoPrintPrinterAsync(
        IPrinterService printerService,
        PosSessionService session,
        string connectionType,
        string? ipAddress = null,
        int port = 9100,
        string? usbDeviceId = null,
        string? usbDeviceName = null,
        bool printerEnabled = true,
        bool cashDrawerEnabled = false)
    {
        var pin = ShiftSession.IsSupervisorRole(session.Role) ? null : "9999";
        var res = await printerService.SaveConfigAsync(new LocalPrinterHardwareConfig
        {
            TerminalId = 1,
            PrinterEnabled = printerEnabled,
            ConnectionType = connectionType,
            CashDrawerEnabled = cashDrawerEnabled,
            PrinterIpAddress = ipAddress,
            PrinterPort = port,
            UsbDeviceId = usbDeviceId,
            UsbDeviceName = usbDeviceName,
            PaperWidthMm = 80,
            ConnectTimeoutMs = 1500,
            AutoCutEnabled = true,
            KickPin = 0,
            PulseOnUnits = 25,
            PulseOffUnits = 250
        }, pin);
        Assert(res.Success, $"Expected printer config save to succeed, got: {res.Message}");
    }

    private static async Task TestAutoPrint01_PaymentMethodsSplitAndSavedReceiptFormatting()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var branch = await localDb.Branches.FirstAsync(b => b.BranchId == 1);
            branch.BranchTagline = "Freshly Brewed Daily";
            await localDb.SaveChangesAsync();
            if (session.Terminal != null) session.Terminal.BranchTagline = "Freshly Brewed Daily";

            var printerSvc = CreateUnifiedService(transport, session);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Ethernet,
                ipAddress: "127.0.0.1", port: printer.Port, printerEnabled: true, cashDrawerEnabled: false);

            var checkout = new OfflineCheckoutService(localDb, session);

            // 1. Cash sale
            var cashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 150m, "CASH", null, "cashier1");
            Assert(cashSale.Success && !string.IsNullOrWhiteSpace(cashSale.LocalTransactionId), "Cash sale must persist.");

            // 2. Card sale
            var cardSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "CARD-APR-001", "cashier1");
            Assert(cardSale.Success, "Card sale must persist.");

            // 3. E-wallet sale
            var ewalletSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "GCASH", "GC-8899", "cashier1");
            Assert(ewalletSale.Success, "GCash sale must persist.");

            // 4. Split sale (Cash + GCash)
            var splitOrder = CreateSampleOrderDto("cashier1", new List<PaymentSplitDto>
            {
                new() { PaymentMethod = "CASH", Amount = 40m, AmountTendered = 50m },
                new() { PaymentMethod = "GCASH", Amount = 60m, ReferenceNumber = "GC-SPLIT-60" }
            });
            var splitSale = await checkout.ProcessSaleAsync(splitOrder, 100m, "SPLIT", null, "cashier1");
            Assert(splitSale.Success, "Split sale must persist.");

            // Verify GetSavedReceiptByTransactionIdAsync reads committed data cleanly
            var savedSplitReceipt = await checkout.GetSavedReceiptByTransactionIdAsync(splitSale.LocalTransactionId!);
            Assert(savedSplitReceipt != null && savedSplitReceipt.Payments.Count == 2,
                "Saved receipt reconstruction must return all split payments from SQLite.");
            Assert(savedSplitReceipt!.BranchTagline == "Freshly Brewed Daily",
                "Saved receipt must preserve BranchTagline.");

            var sales = new[] { cashSale, cardSale, ewalletSale, splitSale };
            int expectedCount = 0;
            foreach (var sale in sales)
            {
                var printRes = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(sale);
                Assert(printRes.Success && !printRes.Skipped && printRes.JobStatus == ReceiptPrintJobStatuses.Printed,
                    $"Expected auto-print PRINTED for {sale.InvoiceNumber}, got Success={printRes.Success}, Skipped={printRes.Skipped}, Status={printRes.JobStatus}, Msg={printRes.Message}");

                expectedCount++;
                await WaitForPayloadsAsync(printer, expectedCount);
                byte[] payload = printer.LastPayload!;
                string text = Encoding.ASCII.GetString(payload);

                Assert(!ContainsDrawerKickSequence(payload), "Auto receipt print must NEVER embed ESC/POS cash drawer kick bytes.");
                Assert(text.Contains(ReceiptCompanyInfo.CompanyName, StringComparison.Ordinal), "Receipt must include AGRES Business Solution.");
                Assert(text.Contains(ReceiptCompanyInfo.Email, StringComparison.Ordinal), "Receipt must include company email.");
                Assert(text.Contains("Freshly Brewed Daily", StringComparison.Ordinal), "Receipt must preserve BranchTagline.");
                Assert(!text.Contains("BR-01", StringComparison.Ordinal), "Receipt must omit BranchCode.");
                Assert(text.Contains(sale.InvoiceNumber!, StringComparison.Ordinal), "Receipt must print invoice number.");
                Assert(text.Contains("Classic Burger", StringComparison.Ordinal), "Receipt must include purchased items.");
            }

            string splitText = Encoding.ASCII.GetString(printer.LastPayload!);
            Assert(splitText.Contains("Payment: CASH", StringComparison.Ordinal) && splitText.Contains("Payment: GCASH", StringComparison.Ordinal),
                "Split receipt must print both CASH and GCASH payment lines.");

            var jobs = await localDb.ReceiptPrintJobs.AsNoTracking().ToListAsync();
            Assert(jobs.Count == 4, $"Expected 4 LocalReceiptPrintJobs rows, got {jobs.Count}");
            Assert(jobs.All(j => j.Status == ReceiptPrintJobStatuses.Printed && j.AttemptCount == 1 && j.PrintedAtUtc.HasValue && j.ConnectionType == PrinterConnectionModes.Ethernet),
                "All 4 print jobs must record PRINTED status, AttemptCount=1, PrintedAtUtc, and Ethernet connection type.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestAutoPrint02_FailedPendingCancelledIncompleteAndReprintSkipped()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var printerSvc = CreateUnifiedService(transport, session);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Ethernet,
                ipAddress: "127.0.0.1", port: printer.Port, printerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);

            // 1. Failed payment (insufficient tender) -> skipped
            var failedSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 10m, "CASH", null, "cashier1");
            Assert(!failedSale.Success, "Insufficient cash sale must fail.");
            var r1 = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(failedSale);
            Assert(r1.Skipped && printer.Count == 0, "Failed sale must NOT auto-print.");

            // 2. Non-persisted synthetic result (not saved in SQLite) -> skipped
            var missingTxResult = new OfflineCheckoutResultDto
            {
                Success = true,
                LocalTransactionId = Guid.NewGuid().ToString(),
                InvoiceNumber = "UNSAVED-001",
                Receipt = new ReceiptDto { TerminalId = 1, InvoiceNumber = "UNSAVED-001" }
            };
            var r2 = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(missingTxResult);
            Assert(r2.Skipped && printer.Count == 0, "Transaction absent from SQLite must NOT auto-print.");

            // 3. PENDING, FAILED, and CANCELLED transactions persisted in SQLite -> never auto-print
            foreach (var (paymentStatus, orderStatus) in new[]
            {
                (PaymentStatuses.Pending, "PENDING"),
                (PaymentStatuses.Failed, "FAILED"),
                (PaymentStatuses.Cancelled, "CANCELLED")
            })
            {
                string id = Guid.NewGuid().ToString();
                localDb.Orders.Add(new LocalOrder
                {
                    OrderId = id,
                    OrderNumber = $"POS01-{id[..4]}",
                    InvoiceNumber = $"INV-{id[..4]}",
                    BranchId = 1,
                    OutletId = 1,
                    TerminalId = 1,
                    CashierId = "cashier1",
                    Subtotal = 100m,
                    TotalAmount = 100m,
                    PaymentStatus = paymentStatus,
                    OrderStatus = orderStatus,
                    CreatedAt = DateTime.UtcNow
                });
                localDb.OrderItems.Add(new LocalOrderItem
                {
                    OrderId = id,
                    ProductId = 1,
                    ProductName = "Classic Burger",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    LineTotal = 100m
                });
                localDb.Payments.Add(new LocalPayment
                {
                    OrderId = id,
                    Amount = 100m,
                    TenderedAmount = 100m,
                    PaymentMethod = "CARD",
                    PaymentStatus = paymentStatus
                });
                await localDb.SaveChangesAsync();

                var syntheticRes = new OfflineCheckoutResultDto { Success = true, LocalTransactionId = id };
                var skipRes = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(syntheticRes);
                Assert(skipRes.Skipped && printer.Count == 0,
                    $"Transaction in state {paymentStatus}/{orderStatus} must NOT auto-print.");
            }

            // 4. Reprint receipt flag -> must NOT auto-print automatically
            var validSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "CARD-9", "cashier1");
            validSale.Receipt!.IsReprint = true;
            var rReprint = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(validSale);
            Assert(rReprint.Skipped && printer.Count == 0, "Reprint receipt must NOT trigger automatic printing.");

            Assert(await localDb.ReceiptPrintJobs.CountAsync() == 0, "None of the non-eligible cases may create print jobs.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestAutoPrint03_DuplicateProtectionRestartAndSyncIsolation()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var printerSvc = CreateUnifiedService(transport, session);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Ethernet,
                ipAddress: "127.0.0.1", port: printer.Port, printerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CASH", null, "cashier1");
            Assert(sale.Success && !string.IsNullOrWhiteSpace(sale.LocalTransactionId), "Sale must succeed.");

            // 1. 5 concurrent callbacks / clicks for same transaction -> exactly ONE auto-print
            var parallelResults = await Task.WhenAll(
                Enumerable.Range(0, 5).Select(_ => Task.Run(() => printerSvc.AutoPrintReceiptForCompletedSaleAsync(sale))));
            Assert(parallelResults.Count(r => !r.Skipped && r.Success) == 1,
                "Concurrent callbacks for one sale must execute at most once.");
            Assert(parallelResults.Count(r => r.Skipped) == 4,
                "Other 4 concurrent callbacks must be skipped cleanly.");

            await WaitForPayloadsAsync(printer, 1);

            // 2. Simulated application restart (clear RAM guards + fresh service instance)
            EscPosPrinterService.ClearInMemoryClaimsForTests();
            var restartedSvc = CreateUnifiedService(transport, session);
            var restartRes = await restartedSvc.AutoPrintReceiptForCompletedSaleAsync(sale);
            Assert(restartRes.Skipped && restartRes.AlreadyPrinted && printer.Count == 1,
                "App restart must NOT automatically reprint a previously printed receipt.");

            // 3. Simulate sync completion (mark LocalSyncOutbox as SYNCED) & verify replay still skips
            var outbox = await localDb.SyncOutboxes.FirstOrDefaultAsync(o => o.EntityId == sale.LocalTransactionId);
            Assert(outbox != null, "Offline sale must enqueue sync outbox item without blocking print.");
            outbox!.Status = "SYNCED";
            outbox.SyncedAt = DateTime.UtcNow;
            await localDb.SaveChangesAsync();

            var postSyncRes = await restartedSvc.AutoPrintReceiptForCompletedSaleAsync(sale);
            Assert(postSyncRes.Skipped && postSyncRes.AlreadyPrinted && printer.Count == 1,
                "Sync completion must NOT trigger duplicate printing.");

            var jobs = await localDb.ReceiptPrintJobs.AsNoTracking().Where(j => j.TransactionId == sale.LocalTransactionId).ToListAsync();
            Assert(jobs.Count == 1 && jobs[0].AttemptCount == 1 && jobs[0].Status == ReceiptPrintJobStatuses.Printed,
                "Exactly 1 LocalReceiptPrintJob row with AttemptCount=1 must exist.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestAutoPrint04_UsbAutoPrintOfflineAndDisabledFallbackBehavior()
    {
        var usb = new SimulatedUsbPrinterTransport();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var printerSvc = CreateUnifiedService(transport, session, usb);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Usb,
                usbDeviceId: usb.ActiveDeviceId, usbDeviceName: usb.DisplayName, printerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);

            var cashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 120m, "CASH", null, "cashier1");
            var res1 = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(cashSale);
            Assert(res1.Success && !res1.Skipped && res1.JobStatus == ReceiptPrintJobStatuses.Printed,
                $"USB Cash auto-print failed: {res1.Message}");

            var cardSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "VISA-1", "cashier1");
            var res2 = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(cardSale);
            Assert(res2.Success && !res2.Skipped && res2.JobStatus == ReceiptPrintJobStatuses.Printed,
                $"USB Card auto-print failed: {res2.Message}");

            Assert(usb.SentPayloads.Count == 2, $"Expected 2 USB print payloads, got {usb.SentPayloads.Count}");
            string payloadText = Encoding.ASCII.GetString(usb.SentPayloads[1]);
            Assert(payloadText.Contains(ReceiptCompanyInfo.CompanyName, StringComparison.Ordinal)
                   && !ContainsDrawerKickSequence(usb.SentPayloads[1]),
                "USB auto-print must contain receipt content and zero drawer kick bytes.");

            var job = await printerSvc.GetReceiptPrintJobAsync(cardSale.LocalTransactionId!);
            Assert(job != null && job.ConnectionType == PrinterConnectionModes.Usb && job.Status == ReceiptPrintJobStatuses.Printed,
                "USB auto-print job must persist ConnectionType=USB and Status=PRINTED.");

            // Direct ESC/POS disabled -> skips auto-print cleanly so OS dialog isn't forced
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Usb,
                usbDeviceId: usb.ActiveDeviceId, usbDeviceName: usb.DisplayName, printerEnabled: false);
            var saleWhenDisabled = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CASH", null, "cashier1");
            var resDisabled = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(saleWhenDisabled);
            Assert(resDisabled.Success && resDisabled.Skipped && usb.SentPayloads.Count == 2,
                "When Direct ESC/POS printing is disabled, auto-print must skip cleanly without sending bytes.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestAutoPrint05_PrinterDisconnectedPreservesSaleAndControlledRetry()
    {
        var usb = new SimulatedUsbPrinterTransport { Connected = false };
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var warnings = new List<string>();
            var printerSvc = CreateUnifiedService(transport, session, usb);
            printerSvc.OnPrinterWarning += w => warnings.Add(w);

            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Usb,
                usbDeviceId: usb.ActiveDeviceId, usbDeviceName: usb.DisplayName, printerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "GCASH", "GC-OFFLINE", "cashier1");
            Assert(sale.Success && !string.IsNullOrWhiteSpace(sale.LocalTransactionId), "Sale must save even if printer will fail.");

            // 1. Disconnected printer failure
            var failRes = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(sale);
            Assert(!failRes.Success && failRes.JobStatus == ReceiptPrintJobStatuses.Failed,
                "Disconnected printer must report FAILED print status.");
            Assert(failRes.Message.Contains("Payment successful, but receipt printing failed", StringComparison.Ordinal),
                $"Failure message must notify cashier safely; got: {failRes.Message}");
            Assert(warnings.Count == 1, "OnPrinterWarning must fire once on failure.");

            // Verify sale in SQLite was NOT cancelled, reversed, or marked unpaid
            var orderRow = await localDb.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.OrderId == sale.LocalTransactionId);
            Assert(orderRow != null && orderRow.PaymentStatus == PaymentStatuses.Paid && orderRow.OrderStatus == "COMPLETED",
                "Failed printing must NEVER invalidate, delete, or unpay the saved transaction.");

            var failedJob = await printerSvc.GetReceiptPrintJobAsync(sale.LocalTransactionId!);
            Assert(failedJob != null
                   && failedJob.Status == ReceiptPrintJobStatuses.Failed
                   && failedJob.AttemptCount == 1
                   && failedJob.ConnectionType == PrinterConnectionModes.Usb
                   && failedJob.TerminalId == 1
                   && !string.IsNullOrWhiteSpace(failedJob.ErrorMessage),
                "Failed print job must record Status=FAILED, AttemptCount=1, ConnectionType, TerminalId, and ErrorMessage.");

            // 2. Restart / duplicate auto-print call after failure does NOT infinite-retry automatically
            EscPosPrinterService.ClearInMemoryClaimsForTests();
            var restartedSvc = CreateUnifiedService(transport, session, usb);
            var autoReplayAfterFail = await restartedSvc.AutoPrintReceiptForCompletedSaleAsync(sale);
            Assert(autoReplayAfterFail.Skipped && autoReplayAfterFail.JobStatus == ReceiptPrintJobStatuses.Failed,
                "Auto-print must NOT auto-retry in an infinite loop on restart; only controlled retry may re-run it.");

            // 3. Printer reconnected -> Controlled Retry transitions FAILED -> PRINTED
            usb.Connected = true;
            var retryRes = await restartedSvc.RetryAutoPrintReceiptAsync(sale.LocalTransactionId!);
            Assert(retryRes.Success && retryRes.JobStatus == ReceiptPrintJobStatuses.Printed,
                $"Controlled Retry Print must succeed once printer reconnects, got: {retryRes.Message}");
            Assert(usb.SentPayloads.Count == 1, "Controlled Retry Print must emit 1 receipt payload.");

            var printedJob = await restartedSvc.GetReceiptPrintJobAsync(sale.LocalTransactionId!);
            Assert(printedJob != null && printedJob.Status == ReceiptPrintJobStatuses.Printed
                   && printedJob.AttemptCount == 2
                   && printedJob.PrintedAtUtc.HasValue
                   && string.IsNullOrEmpty(printedJob.ErrorMessage),
                "Successful retry must update Status=PRINTED, AttemptCount=2, PrintedAtUtc, and clear ErrorMessage.");

            // 4. Extra click on Retry Print after PRINTED must skip duplicate auto-retry
            var secondRetry = await restartedSvc.RetryAutoPrintReceiptAsync(sale.LocalTransactionId!);
            Assert(secondRetry.Skipped && secondRetry.AlreadyPrinted && usb.SentPayloads.Count == 1,
                "RetryAutoPrintReceiptAsync must block accidental duplicate retry once PRINTED.");

            // 5. Intentional manual Reprint still works anytime
            var savedReceipt = await checkout.GetSavedReceiptByTransactionIdAsync(sale.LocalTransactionId!);
            savedReceipt!.IsReprint = true;
            var manualReprint = await restartedSvc.PrintReceiptAsync(savedReceipt);
            Assert(manualReprint.Success && usb.SentPayloads.Count == 2,
                "Manual receipt reprint must always remain available after auto-print.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestAutoPrint06_CashDrawerCoordinationAndReprintIsolation()
    {
        await using var printer = new LoopbackPrinterServer();
        using var transport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            var printerSvc = CreateUnifiedService(transport, session);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Ethernet,
                ipAddress: "127.0.0.1", port: printer.Port, printerEnabled: true, cashDrawerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);

            // 1. Cash sale: Drawer opens once AND Receipt prints once independently
            var cashSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CASH", null, "cashier1");
            var drawerCashRes = await printerSvc.OpenCashDrawerForCompletedSaleAsync(cashSale);
            var printCashRes = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(cashSale);
            Assert(drawerCashRes.Success && !drawerCashRes.Skipped, "Cash sale must open cash drawer.");
            Assert(printCashRes.Success && !printCashRes.Skipped, "Cash sale must auto-print receipt.");
            await WaitForPayloadsAsync(printer, 2);

            Assert(printer.ReceivedPayloads[0].SequenceEqual(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }),
                "Payload 1 must be the exact ESC/POS drawer kick.");
            Assert(!ContainsDrawerKickSequence(printer.ReceivedPayloads[1]),
                "Payload 2 (auto receipt) must NOT contain drawer kick bytes.");

            // 2. Card-only sale: Drawer skipped, Receipt auto-printed once
            var cardSale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "VISA-99", "cashier1");
            var drawerCardRes = await printerSvc.OpenCashDrawerForCompletedSaleAsync(cardSale);
            var printCardRes = await printerSvc.AutoPrintReceiptForCompletedSaleAsync(cardSale);
            Assert(drawerCardRes.Skipped, "Card-only sale must NOT open cash drawer.");
            Assert(printCardRes.Success && !printCardRes.Skipped, "Card-only sale MUST auto-print receipt.");
            await WaitForPayloadsAsync(printer, 3);
            Assert(!ContainsDrawerKickSequence(printer.ReceivedPayloads[2]),
                "Card-only receipt payload must NOT contain drawer kick bytes.");

            // 3. Manual Reprint NEVER triggers cash drawer opening
            var reprintDto = await checkout.GetSavedReceiptByTransactionIdAsync(cashSale.LocalTransactionId!);
            reprintDto!.IsReprint = true;
            var reprintRes = await printerSvc.PrintReceiptAsync(reprintDto);
            Assert(reprintRes.Success, $"Manual reprint failed: {reprintRes.Message}");
            await WaitForPayloadsAsync(printer, 4);
            Assert(!ContainsDrawerKickSequence(printer.ReceivedPayloads[3]),
                "Receipt reprint must NEVER trigger cash drawer opening.");
            Assert(Encoding.ASCII.GetString(printer.ReceivedPayloads[3]).Contains("** REPRINT **", StringComparison.Ordinal),
                "Manual reprint payload must clearly mark ** REPRINT **.");

            // Verify independent hardware audit trails in SQLite
            int autoDrawerCount = await localDb.CashDrawerHardwareEvents.CountAsync(e => e.ActionType == "AUTO_SALE_CASH");
            int autoPrintCount = await localDb.ReceiptPrintJobs.CountAsync(j => j.Status == ReceiptPrintJobStatuses.Printed);
            Assert(autoDrawerCount == 1 && autoPrintCount == 2,
                $"Expected 1 AUTO_SALE_CASH drawer event and 2 auto-print jobs, got drawer={autoDrawerCount}, print={autoPrintCount}");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private sealed class HangingNetworkTransport : IEscPosNetworkTransport
    {
        public async Task SendBytesAsync(string host, int port, ReadOnlyMemory<byte> payload, int timeoutMs, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class SingleThreadUiSyncContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => ThreadPool.QueueUserWorkItem(_ => d(state));
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private static async Task TestAutoPrint07_HungTransportAndSynchronizationContextNeverStall()
    {
        var prevSync = SynchronizationContext.Current;
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv();
        try
        {
            SynchronizationContext.SetSynchronizationContext(new SingleThreadUiSyncContext());
            var printerSvc = CreateUnifiedService(new HangingNetworkTransport(), session);
            await ConfigureUnifiedAutoPrintPrinterAsync(
                printerSvc, session, PrinterConnectionModes.Ethernet,
                ipAddress: "127.0.0.1", port: 9100, printerEnabled: true);

            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("cashier1"), 100m, "CARD", "CARD-HANG", "cashier1");
            Assert(sale.Success && !string.IsNullOrWhiteSpace(sale.LocalTransactionId), "Sale must commit.");

            using var boundedCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
            var res = await Task.Run(() => printerSvc.AutoPrintReceiptForCompletedSaleAsync(sale, boundedCts.Token))
                .WaitAsync(TimeSpan.FromSeconds(4));

            Assert(!res.Success && res.JobStatus == ReceiptPrintJobStatuses.Failed,
                $"Hung/cancelled transport must return FAILED instead of hanging, got Success={res.Success}, Status={res.JobStatus}");

            var job = await printerSvc.GetReceiptPrintJobAsync(sale.LocalTransactionId!);
            Assert(job != null && job.Status == ReceiptPrintJobStatuses.Failed,
                $"Print job must never remain stuck in PRINTING after timeout/cancellation; got {job?.Status}");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prevSync);
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }
}
