using System.Text;
using POS.Client.Data;
using POS.Client.Services;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private sealed class SimulatedUsbPrinterTransport : IUsbPrinterTransport
    {
        public bool Supported { get; set; } = true;
        public bool Connected { get; set; } = true;
        public bool PermissionGranted { get; set; } = true;
        public string ActiveDeviceId { get; set; } = "usb:vid=0483&pid=5740&ser=XP80T-01";
        public string DisplayName { get; set; } = "Xprinter XP-80T USB (VID:0483 PID:5740)";
        public int ConnectFailuresBeforeSuccess { get; set; }
        public List<byte[]> SentPayloads { get; } = new();

        public bool IsSupportedOnPlatform => Supported;

        public Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
        {
            if (!Connected)
                return Task.FromResult<IReadOnlyList<DiscoveredUsbPrinter>>(Array.Empty<DiscoveredUsbPrinter>());

            IReadOnlyList<DiscoveredUsbPrinter> list =
            [
                new DiscoveredUsbPrinter(
                    DeviceId: ActiveDeviceId,
                    DisplayName: DisplayName,
                    VendorId: 0x0483,
                    ProductId: 0x5740,
                    SerialNumber: "XP80T-01",
                    TransportKind: "TEST_USB",
                    RequiresPermission: true,
                    HasPermission: PermissionGranted,
                    IsConnected: true,
                    StatusNote: PermissionGranted ? "USB Ready" : "USB Permission Required")
            ];
            return Task.FromResult(list);
        }

        public Task<bool> EnsurePermissionAsync(string? deviceId, CancellationToken cancellationToken = default)
        {
            if (!Connected || string.IsNullOrWhiteSpace(deviceId))
                return Task.FromResult(false);
            PermissionGranted = true;
            return Task.FromResult(true);
        }

        public Task<PrinterConnectionStatus> CheckStatusAsync(
            string? deviceId,
            string? cachedName,
            int timeoutMs,
            CancellationToken cancellationToken = default)
        {
            bool matching = Connected && !string.IsNullOrWhiteSpace(deviceId) &&
                            string.Equals(deviceId, ActiveDeviceId, StringComparison.OrdinalIgnoreCase);

            return Task.FromResult(new PrinterConnectionStatus(
                IsConfigured: !string.IsNullOrWhiteSpace(deviceId),
                IsConnected: matching && PermissionGranted,
                Mode: PrinterConnectionMode.Usb,
                EndpointDisplay: matching ? DisplayName : (cachedName ?? deviceId ?? "USB"),
                StatusMessage: !matching ? "USB printer disconnected" : (PermissionGranted ? "USB Connected" : "USB Permission Required"),
                RequiresPermission: true,
                PermissionGranted: PermissionGranted));
        }

        public Task SendBytesAsync(
            string? deviceId,
            ReadOnlyMemory<byte> payload,
            int timeoutMs,
            bool allowConnectRetry,
            CancellationToken cancellationToken = default)
        {
            if (!Connected)
                throw new PrinterConnectException("USB printer is unplugged or powered off.");
            if (!PermissionGranted)
                throw new PrinterConnectException("USB printer permission not granted.");

            if (ConnectFailuresBeforeSuccess > 0)
            {
                ConnectFailuresBeforeSuccess--;
                throw new PrinterConnectException("Transient USB endpoint busy before transfer.");
            }

            lock (SentPayloads)
            {
                SentPayloads.Add(payload.ToArray());
            }
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingFallbackReceiptPrinter : IReceiptPrinter
    {
        public int FallbackPrintCount { get; private set; }
        public string? LastPrinterName { get; private set; }

        public Task PrintAsync(ReceiptDto receipt) { FallbackPrintCount++; return Task.CompletedTask; }
        public Task PrintPreparationTicketAsync(PreparationTicketDto ticket, string? printerName = null) { FallbackPrintCount++; LastPrinterName = printerName; return Task.CompletedTask; }
        public Task PrintXReadingAsync(XReadingReportDto report, string? printerName = null) { FallbackPrintCount++; LastPrinterName = printerName; return Task.CompletedTask; }
        public Task<bool> PrintZReadingAsync(ZReadingReportDto report, string? printerName = null, bool isReprint = false) { FallbackPrintCount++; LastPrinterName = printerName; return Task.FromResult(true); }
        public Task PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, string? printerName = null) { FallbackPrintCount++; LastPrinterName = printerName; return Task.CompletedTask; }
    }

    private static byte[] CreateMinimal24BitBmp(int width, int height)
    {
        int rowStride = ((width * 24 + 31) / 32) * 4;
        int imageSize = rowStride * height;
        int fileSize = 54 + imageSize;
        byte[] bmp = new byte[fileSize];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(bmp, 2);
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(width).CopyTo(bmp, 18);
        BitConverter.GetBytes(height).CopyTo(bmp, 22);
        BitConverter.GetBytes((ushort)1).CopyTo(bmp, 26);
        BitConverter.GetBytes((ushort)24).CopyTo(bmp, 28);
        // Leave pixels all 0 (black) so GS v 0 emits packed non-zero 1 bits!
        return bmp;
    }

    private static ReceiptDto CreateSampleReceiptDto() => new()
    {
        OrderId = 42,
        OrderNumber = "ORD-0042",
        InvoiceNumber = "TXN1-000042",
        BranchId = 1,
        BranchName = "Main Branch",
        BranchCode = "SHOULD-NOT-APPEAR",
        BranchTagline = "Best Eats",
        BranchAddress = "Sta. Rosa, Laguna",
        BranchPhone = "0917-000-0000",
        VatDisplay = true,
        TerminalId = 1,
        TerminalName = "POS1",
        CashierId = "cashier1",
        CreatedAt = new DateTime(2026, 11, 15, 4, 30, 0, DateTimeKind.Utc),
        PaidAt = new DateTime(2026, 11, 15, 4, 30, 0, DateTimeKind.Utc),
        Subtotal = 89.29m,
        VatableSales = 89.29m,
        VatAmount = 10.71m,
        TotalAmount = 100.00m,
        Items = new()
        {
            new OrderItemResponseDto
            {
                ProductId = 1,
                ProductName = "Classic Burger",
                Quantity = 1m,
                UnitPrice = 100m,
                LineTotal = 100m
            }
        },
        Payments = new()
        {
            new PaymentResponseDto
            {
                PaymentMethod = "CASH",
                Amount = 100m,
                AmountTendered = 150m,
                ChangeAmount = 50m,
                PaymentStatus = PaymentStatuses.Paid
            }
        }
    };

    private static Task TestUnifiedPrinter01_EscPosFormattingAndBitmap()
    {
        var receipt = CreateSampleReceiptDto();
        var bmp = CreateMinimal24BitBmp(16, 4);
        receipt.ReceiptLogoDataUri = "data:image/bmp;base64," + Convert.ToBase64String(bmp);

        // Notice BasicEscPosImageRasterizer only needs ReceiptLogoValidator to allow png/jpg in HTML,
        // while PrintImageAsync directly encodes raw BMP/raster bytes via GS v 0:
        var cfg80 = new LocalPrinterHardwareConfig
        {
            TerminalId = 1,
            PrinterEnabled = true,
            PaperWidthMm = 80,
            PrintDensity = "Dark",
            CharacterEncoding = "PC850",
            AutoCutEnabled = true
        };
        byte[] bytes80 = EscPosReceiptBuilder.BuildReceipt(receipt, cfg80, new BasicEscPosImageRasterizer());
        string ascii80 = Encoding.ASCII.GetString(bytes80);
        Assert(bytes80[0] == 0x1B && bytes80[1] == 0x40, "ESC @ initialization must start 80mm receipt.");
        Assert(ascii80.Contains("AGRES Business Solution", StringComparison.Ordinal), "Footer company name required.");
        Assert(!ascii80.Contains("SHOULD-NOT-APPEAR", StringComparison.Ordinal), "BranchCode must stay omitted on customer receipts.");
        Assert(ascii80.Contains("------------------------------------------------", StringComparison.Ordinal), "80mm layout must use 48-column divider.");
        Assert(bytes80.AsSpan().IndexOf(new byte[] { 0x1D, 0x28, 0x4B, 0x02, 0x00, 0x31, 0x03 }) >= 0, "Dark density must emit GS ( K.");
        Assert(bytes80.AsSpan().IndexOf(new byte[] { 0x1B, 0x74, 0x02 }) >= 0, "PC850 must emit ESC t 2.");
        Assert(bytes80.AsSpan().EndsWith(new byte[] { 0x1D, 0x56, 0x42, 0x00 }), "AutoCut=true must end with GS V 42 00.");

        // 58mm width & AutoCut=false & UTF-8
        var cfg58 = new LocalPrinterHardwareConfig
        {
            TerminalId = 1,
            PrinterEnabled = true,
            PaperWidthMm = 58,
            PrintDensity = "Normal",
            CharacterEncoding = "UTF-8",
            AutoCutEnabled = false
        };
        byte[] bytes58 = EscPosReceiptBuilder.BuildReceipt(receipt, cfg58);
        string ascii58 = Encoding.ASCII.GetString(bytes58);
        Assert(ascii58.Contains("--------------------------------", StringComparison.Ordinal) &&
               !ascii58.Contains("------------------------------------------------", StringComparison.Ordinal),
            "58mm layout must wrap at 32 columns.");
        Assert(!bytes58.AsSpan().EndsWith(new byte[] { 0x1D, 0x56, 0x42, 0x00 }), "AutoCut=false must not send cut command.");

        // Verify raw raster bitmap packet GS v 0
        byte[] imgPacket = EscPosReceiptBuilder.BuildSingleBitmap(bmp, cfg80, new BasicEscPosImageRasterizer(), cutPaper: false);
        Assert(imgPacket.AsSpan().IndexOf(new byte[] { 0x1D, 0x76, 0x30, 0x00, 0x02, 0x00, 0x04, 0x00 }) >= 0,
            "16x4 BMP must emit exact GS v 0 header (2 bytes wide x 4 rows high).");

        return Task.CompletedTask;
    }

    private static async Task TestUnifiedPrinter02_UsbLifecycleDiscoveryPermissionReconnectAndDrawer()
    {
        using var netTransport = new EscPosTcpTransport();
        var usbTransport = new SimulatedUsbPrinterTransport { PermissionGranted = false };
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("manager1", "Manager");
        try
        {
            var service = CreateUnifiedService(netTransport, session, usbTransport);

            // 1. Discover USB printer + request permission
            var found = await service.DiscoverUsbPrintersAsync();
            Assert(found.Count == 1 && found[0].RequiresPermission && !found[0].HasPermission, "USB device must be discovered before permission.");
            var permRes = await service.RequestUsbPermissionAsync(found[0].DeviceId);
            Assert(permRes.Success && usbTransport.PermissionGranted, "USB permission request must grant permission.");

            // 2. Save terminal config in USB mode
            var save = await service.SaveConfigAsync(new LocalPrinterHardwareConfig
            {
                TerminalId = 1,
                PrinterEnabled = true,
                ConnectionType = PrinterConnectionModes.Usb,
                CashDrawerEnabled = true,
                UsbDeviceId = found[0].DeviceId,
                UsbDeviceName = found[0].DisplayName,
                PaperWidthMm = 80,
                AutoCutEnabled = true,
                KickPin = 0,
                PulseOnUnits = 25,
                PulseOffUnits = 250
            });
            Assert(save.Success, $"USB config save must succeed: {save.Message}");

            // 3. Print test slip & sales receipt & auto cash drawer open via USB
            var testConn = await service.TestConnectionAsync();
            Assert(testConn.Success, "USB TestConnection must succeed when plugged in & permitted.");

            var testReceipt = await service.PrintTestReceiptAsync();
            Assert(testReceipt.Success && usbTransport.SentPayloads.Count == 1, "USB PrintTestReceipt must send bytes.");

            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("manager1"), 100m, "CASH", null, "manager1");
            Assert(sale.Success, "Sale must commit offline.");

            var receiptPrint = await service.PrintReceiptAsync(sale.Receipt!);
            var drawerKick = await service.OpenCashDrawerForCompletedSaleAsync(sale);
            Assert(receiptPrint.Success && drawerKick.Success && usbTransport.SentPayloads.Count == 3,
                "USB must print sales receipt and open cash drawer.");
            Assert(usbTransport.SentPayloads[2].SequenceEqual(new byte[] { 0x1B, 0x70, 0x00, 0x19, 0xFA }),
                "USB cash drawer kick must emit identical 1B 70 00 19 FA bytes as Ethernet.");

            // 4. Unplug USB -> failure must report cleanly & never touch saved sale; reconnect -> recovers immediately!
            usbTransport.Connected = false;
            var disconnectedStatus = await service.GetConnectionStatusAsync();
            Assert(!disconnectedStatus.IsConnected, "Unplugged USB must report disconnected.");
            var failedPrint = await service.PrintTextAsync("Should fail safely");
            Assert(!failedPrint.Success, "Print while USB unplugged must fail gracefully.");

            usbTransport.Connected = true;
            var recoveredStatus = await service.GetConnectionStatusAsync();
            Assert(recoveredStatus.IsConnected, "Reconnected USB must recover automatically.");
            var recoveredPrint = await service.PrintTextAsync("Recovered OK");
            Assert(recoveredPrint.Success && usbTransport.SentPayloads.Count == 4, "Printing must succeed immediately after USB reconnect.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestUnifiedPrinter03_EthernetSwitchingPersistenceAndMultiTerminalIsolation()
    {
        await using var printer = new LoopbackPrinterServer();
        using var netTransport = new EscPosTcpTransport();
        var usbTransport = new SimulatedUsbPrinterTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("manager1", "Manager");
        try
        {
            var svc1 = CreateUnifiedService(netTransport, session, usbTransport);

            // Configure Terminal 1 as Ethernet
            var saveEth = await svc1.SaveConfigAsync(new LocalPrinterHardwareConfig
            {
                TerminalId = 1,
                PrinterEnabled = true,
                ConnectionType = PrinterConnectionModes.Ethernet,
                CashDrawerEnabled = true,
                PrinterIpAddress = "127.0.0.1",
                PrinterPort = printer.Port,
                UsbDeviceId = usbTransport.ActiveDeviceId,
                UsbDeviceName = usbTransport.DisplayName
            });
            Assert(saveEth.Success, "Ethernet save must succeed.");

            // Configure Terminal 2 independently as USB (must NOT leak to Terminal 1!)
            var term2Session = new PosSessionService();
            term2Session.SetSession("MGR-JWT", "manager1", "Manager", new TerminalConfigDto { TerminalId = 2, OutletId = 1, BranchId = 1, TerminalName = "POS2" });
            var svc2 = CreateUnifiedService(netTransport, term2Session, usbTransport);
            var saveUsbT2 = await svc2.SaveConfigAsync(new LocalPrinterHardwareConfig
            {
                TerminalId = 2,
                PrinterEnabled = true,
                ConnectionType = PrinterConnectionModes.Usb,
                CashDrawerEnabled = false,
                UsbDeviceId = usbTransport.ActiveDeviceId,
                UsbDeviceName = usbTransport.DisplayName,
                PaperWidthMm = 58
            });
            Assert(saveUsbT2.Success, "Terminal 2 USB save must succeed independently.");

            // Simulate app restart -> fresh instance restores Terminal 1 Ethernet & Terminal 2 USB separately
            var restartedT1 = CreateUnifiedService(netTransport, session, usbTransport);
            var t1Cfg = await restartedT1.GetConfigAsync(1);
            var t2Cfg = await restartedT1.GetConfigAsync(2);
            Assert(t1Cfg.ConnectionType == PrinterConnectionModes.Ethernet && t1Cfg.PrinterPort == printer.Port,
                "Terminal 1 Ethernet config must survive restart.");
            Assert(t2Cfg.ConnectionType == PrinterConnectionModes.Usb && t2Cfg.PaperWidthMm == 58,
                "Terminal 2 USB config must stay isolated from Terminal 1.");

            var connRes = await restartedT1.TestConnectionAsync();
            Assert(connRes.Success, $"Ethernet TestConnection must probe loopback port {printer.Port} cleanly.");

            // Same receipt bytes over Ethernet vs USB!
            var fallbackPrinter = new CapturingFallbackReceiptPrinter();
            var unified = new UnifiedReceiptPrinter(restartedT1, session, () => fallbackPrinter);
            await unified.PrintAsync(CreateSampleReceiptDto());
            for (int w = 0; w < 20 && printer.Count == 0; w++) await Task.Delay(25);
            Assert(printer.Count == 1 && fallbackPrinter.FallbackPrintCount == 0,
                $"Enabled unified printer must route directly to ESC/POS without invoking OS WebView fallback (count={printer.Count}, fallback={fallbackPrinter.FallbackPrintCount}).");

            // Switch Terminal 1 to USB -> sends exact same receipt flow via USB without changing Ethernet fields!
            t1Cfg.ConnectionType = PrinterConnectionModes.Usb;
            await restartedT1.SaveConfigAsync(t1Cfg);
            await unified.PrintAsync(CreateSampleReceiptDto());
            Assert(usbTransport.SentPayloads.Count == 1 && usbTransport.SentPayloads[0].Length > 100,
                "Switching Terminal 1 from Ethernet to USB must immediately route receipts over USB.");

            // Disable PrinterEnabled -> delegates cleanly to OS WebView fallback!
            t1Cfg.PrinterEnabled = false;
            await restartedT1.SaveConfigAsync(t1Cfg);
            await unified.PrintAsync(CreateSampleReceiptDto());
            Assert(fallbackPrinter.FallbackPrintCount == 1,
                "Disabling direct ESC/POS printing must fall back to OS printer.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestUnifiedPrinter04_RetryBoundariesConcurrencyAndTimeoutSafety()
    {
        using var netTransport = new EscPosTcpTransport();
        var usbTransport = new SimulatedUsbPrinterTransport { ConnectFailuresBeforeSuccess = 1 };
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("manager1", "Manager");
        try
        {
            var service = CreateUnifiedService(netTransport, session, usbTransport);
            await service.SaveConfigAsync(new LocalPrinterHardwareConfig
            {
                TerminalId = 1,
                PrinterEnabled = true,
                ConnectionType = PrinterConnectionModes.Usb,
                CashDrawerEnabled = true,
                UsbDeviceId = usbTransport.ActiveDeviceId,
                UsbDeviceName = usbTransport.DisplayName
            });

            // 1. Pre-write connect failure for receipt printing retries cleanly ONCE and succeeds
            var printRes = await service.PrintTextAsync("Pre-write connect retry works");
            Assert(printRes.Success && usbTransport.SentPayloads.Count == 1,
                "Transient pre-write connect error on print job must recover via capped single retry.");

            // 2. Cash drawer kick NEVER retries even if pre-write connect blips (anti-double-open rule)
            usbTransport.ConnectFailuresBeforeSuccess = 1;
            var checkout = new OfflineCheckoutService(localDb, session);
            var sale = await checkout.ProcessSaleAsync(CreateSampleOrderDto("manager1"), 100m, "CASH", null, "manager1");
            Assert(sale.Success, "Sale must commit.");
            var kickRes = await service.OpenCashDrawerForCompletedSaleAsync(sale);
            Assert(!kickRes.Success && usbTransport.SentPayloads.Count == 1,
                "Cash drawer kick must NOT auto-retry so physical drawer can never double-open.");

            // 3. Subsequent manual drawer open recovers cleanly and serializes concurrent print calls
            usbTransport.ConnectFailuresBeforeSuccess = 0;
            var concurrent = await Task.WhenAll(
                service.PrintTextAsync("Job 1"),
                service.PrintTextAsync("Job 2"),
                service.ManualOpenCashDrawerAsync("Verify concurrency serialization"));
            Assert(concurrent.All(r => r.Success) && usbTransport.SentPayloads.Count == 4,
                "Concurrent print and drawer calls must serialize safely without failure or interleaving.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }

    private static async Task TestUnifiedPrinter05_OfflineOperationWithoutApiAndAllReportTypes()
    {
        await using var printer = new LoopbackPrinterServer();
        using var netTransport = new EscPosTcpTransport();
        var (tempDb, prevPath, localDb, session) = CreateOfflineDrawerTestEnv("manager1", "Manager");
        try
        {
            var service = CreateUnifiedService(netTransport, session);
            await service.SaveConfigAsync(new LocalPrinterHardwareConfig
            {
                TerminalId = 1,
                PrinterEnabled = true,
                ConnectionType = PrinterConnectionModes.Ethernet,
                CashDrawerEnabled = true,
                PrinterIpAddress = "127.0.0.1",
                PrinterPort = printer.Port
            });

            var prepRes = await service.PrintPreparationTicketAsync(new PreparationTicketDto
            {
                Station = "KITCHEN",
                OrderNumber = "ORD-1",
                TerminalName = "POS1",
                CashierId = "cashier1",
                CreatedAt = DateTime.UtcNow,
                Items = new() { new PreparationTicketItemDto { ProductName = "Adobo", Quantity = 2m } }
            });
            var xRes = await service.PrintXReadingAsync(new XReadingReportDto
            {
                BranchName = "Main",
                TerminalName = "POS1",
                CashierId = "cashier1",
                ShiftId = 101,
                OpenedAt = DateTime.UtcNow,
                GeneratedAt = DateTime.UtcNow,
                OverallStatus = "BALANCED"
            });
            var zRes = await service.PrintZReadingAsync(new ZReadingReportDto
            {
                BranchName = "Main",
                OutletName = "Main",
                TerminalCode = "POS-1",
                TerminalName = "POS1",
                GeneratedBy = "manager1",
                BusinessDate = DateOnly.FromDateTime(DateTime.Today),
                GeneratedAt = DateTime.UtcNow
            }, isReprint: true);
            var declRes = await service.PrintCashierDeclarationAsync(new CashierDeclarationReportDto
            {
                BranchName = "Main",
                TerminalName = "POS1",
                ShiftId = 101,
                CashierId = "cashier1",
                OpenedAt = DateTime.UtcNow,
                SubmittedAt = DateTime.UtcNow
            });

            Assert(prepRes.Success && xRes.Success && zRes.Success && declRes.Success && printer.Count == 4,
                "All ESC/POS document types (Prep, X-Reading, Z-Reading, Cashier Declaration) must print offline.");
            Assert(Encoding.ASCII.GetString(printer.ReceivedPayloads[2]).Contains("REPRINT", StringComparison.Ordinal),
                "Z-Reading reprint flag must appear in ESC/POS output.");
        }
        finally
        {
            localDb.Dispose();
            AppContext.SetData("POS.LocalDatabasePath", prevPath);
            try { File.Delete(tempDb); } catch { }
        }
    }
}
