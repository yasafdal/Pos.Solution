using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using POS.Client.Data;
using POS.Shared.Constants;
using POS.Shared.Interfaces;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

/// <summary>
/// Unified ESC/POS thermal printer and RJ11/RJ12 cash drawer service supporting BOTH USB and Ethernet.
/// Single owner of terminal hardware configuration, global print/kick serialization, idempotent per-transaction
/// cash drawer opening, supervisor-gated manual open, and local SQLite hardware audit logging.
/// </summary>
public sealed class EscPosPrinterService : IPrinterService, ICashDrawerHardwareService
{
    // Global serialization lock prevents interleaved raw ESC/POS bytes across print jobs and drawer kicks.
    private static readonly SemaphoreSlim GlobalHardwareJobGate = new(1, 1);

    // Coalesces duplicate callbacks/re-renders per database path + transaction ID across service instances.
    private static readonly ConcurrentDictionary<string, byte> InFlightOrProcessedClaims = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> InFlightAutoPrintClaims = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> InFlightManualPrintRetries = new(StringComparer.Ordinal);

    private readonly EthernetPrinterConnection _ethernetConnection;
    private readonly UsbPrinterConnection _usbConnection;
    private readonly IUsbPrinterTransport _usbTransport;
    private readonly IEscPosImageRasterizer _imageRasterizer;
    private readonly PosSessionService _session;
    private readonly IUserService? _userService;
    private readonly ILogger<EscPosPrinterService>? _logger;

    public event Action<string>? OnPrinterWarning;
    public event Action<string>? OnCashierWarning;

    public EscPosPrinterService(
        EthernetPrinterConnection ethernetConnection,
        UsbPrinterConnection usbConnection,
        IUsbPrinterTransport usbTransport,
        PosSessionService session,
        IEscPosImageRasterizer? imageRasterizer = null,
        IUserService? userService = null,
        ILogger<EscPosPrinterService>? logger = null)
    {
        _ethernetConnection = ethernetConnection ?? throw new ArgumentNullException(nameof(ethernetConnection));
        _usbConnection = usbConnection ?? throw new ArgumentNullException(nameof(usbConnection));
        _usbTransport = usbTransport ?? throw new ArgumentNullException(nameof(usbTransport));
        _imageRasterizer = imageRasterizer ?? new BasicEscPosImageRasterizer();
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _userService = userService;
        _logger = logger;
    }

    internal static void ClearInMemoryClaimsForTests()
    {
        InFlightOrProcessedClaims.Clear();
        InFlightAutoPrintClaims.Clear();
        InFlightManualPrintRetries.Clear();
    }

    /// <summary>
    /// Returns true ONLY when checkout succeeded cleanly with a local transaction ID
    /// and is not a reprint, void, or return.
    /// </summary>
    public static bool ShouldAutoPrintForCompletedSale(OfflineCheckoutResultDto? result)
    {
        if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.LocalTransactionId))
            return false;

        var receipt = result.Receipt;
        if (receipt != null && (receipt.IsReprint || receipt.IsVoid || receipt.IsReturn))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true ONLY when the sale committed successfully, is not a reprint/void/return,
    /// and contains at least one finalized PAID payment line for CASH with a positive amount.
    /// </summary>
    public static bool ShouldOpenForCompletedSale(OfflineCheckoutResultDto? result)
    {
        if (result == null || !result.Success || string.IsNullOrWhiteSpace(result.LocalTransactionId))
            return false;

        var receipt = result.Receipt;
        if (receipt == null || receipt.IsReprint || receipt.IsVoid || receipt.IsReturn)
            return false;

        if (receipt.Payments == null || receipt.Payments.Count == 0)
            return false;

        return receipt.Payments.Any(p =>
            p.Amount > 0m &&
            string.Equals(p.PaymentStatus?.Trim(), PaymentStatuses.Paid, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.PaymentMethod?.Trim(), "CASH", StringComparison.OrdinalIgnoreCase));
    }

    public async Task<LocalPrinterHardwareConfig> GetConfigAsync(int terminalId)
    {
        if (terminalId <= 0) terminalId = 1;
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        var existing = await db.PrinterHardwareConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.TerminalId == terminalId);
        return existing ?? new LocalPrinterHardwareConfig
        {
            TerminalId = terminalId,
            PrinterEnabled = false,
            ConnectionType = PrinterConnectionModes.Ethernet,
            CashDrawerEnabled = false,
            PrinterPort = EscPosCommands.DefaultPort,
            PaperWidthMm = EscPosCommands.PaperWidth80Mm,
            PrintDensity = "Normal",
            CharacterEncoding = "PC437",
            AutoCutEnabled = true,
            ConnectTimeoutMs = EscPosCommands.DefaultTimeoutMs,
            KickPin = EscPosCommands.DefaultKickPin,
            PulseOnUnits = EscPosCommands.DefaultPulseOnUnits,
            PulseOffUnits = EscPosCommands.DefaultPulseOffUnits
        };
    }

    public async Task<CashDrawerActionResult> SaveConfigAsync(
        LocalPrinterHardwareConfig config,
        string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var auth = await AuthorizeSupervisorActionAsync(supervisorPin, allowOfflinePin: true, cancellationToken);
        if (!auth.Authorized)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Unauthorized = true,
                Message = auth.ErrorMessage ?? "Supervisor authorization is required to modify printer and drawer settings."
            };
        }

        string modeString = PrinterConnectionModes.Normalize(config.ConnectionType);
        bool isUsb = modeString == PrinterConnectionModes.Usb;
        bool activeRequired = config.PrinterEnabled || config.CashDrawerEnabled;

        string? normalizedHost = config.PrinterIpAddress?.Trim();
        if (!isUsb)
        {
            if (activeRequired || !string.IsNullOrWhiteSpace(config.PrinterIpAddress))
            {
                if (!EscPosCommands.TryValidateEndpoint(config.PrinterIpAddress, config.PrinterPort, out var host, out var endpointErr))
                    return new CashDrawerActionResult { Success = false, Message = endpointErr ?? "Invalid Ethernet printer endpoint." };
                normalizedHost = host;
            }
            else if (config.PrinterPort < 1 || config.PrinterPort > 65535)
            {
                return new CashDrawerActionResult { Success = false, Message = "Printer TCP port must be between 1 and 65535." };
            }
        }
        else
        {
            if (activeRequired && !_usbTransport.IsSupportedOnPlatform)
                return new CashDrawerActionResult { Success = false, Message = "USB ESC/POS printing is not supported on this platform." };

            if (activeRequired && string.IsNullOrWhiteSpace(config.UsbDeviceId))
                return new CashDrawerActionResult { Success = false, Message = "Select a USB printer before enabling USB printing or cash drawer." };

            if (config.PrinterPort < 1 || config.PrinterPort > 65535)
                config.PrinterPort = EscPosCommands.DefaultPort;
        }

        byte[] kickBytes;
        try
        {
            kickBytes = EscPosCommands.BuildCashDrawerKick(config.KickPin, config.PulseOnUnits, config.PulseOffUnits);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return new CashDrawerActionResult { Success = false, Message = ex.Message };
        }

        int termId = config.TerminalId > 0 ? config.TerminalId : (_session.Terminal?.TerminalId ?? 1);
        int boundedTimeout = EscPosCommands.ClampTimeoutMs(config.ConnectTimeoutMs);
        int paperWidth = EscPosCommands.NormalizePaperWidthMm(config.PaperWidthMm);
        string density = EscPosCommands.NormalizeDensity(config.PrintDensity);
        string encoding = EscPosCommands.NormalizeEncoding(config.CharacterEncoding);
        string? usbId = string.IsNullOrWhiteSpace(config.UsbDeviceId) ? null : config.UsbDeviceId.Trim();
        string? usbName = string.IsNullOrWhiteSpace(config.UsbDeviceName) ? null : config.UsbDeviceName.Trim();

        await LocalPosDbContext.WriteLock.WaitAsync(cancellationToken);
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var entity = await db.PrinterHardwareConfigs.FirstOrDefaultAsync(c => c.TerminalId == termId, cancellationToken);
            if (entity == null)
            {
                entity = new LocalPrinterHardwareConfig { TerminalId = termId };
                db.PrinterHardwareConfigs.Add(entity);
            }

            entity.PrinterEnabled = config.PrinterEnabled;
            entity.ConnectionType = modeString;
            entity.CashDrawerEnabled = config.CashDrawerEnabled;
            entity.PrinterIpAddress = normalizedHost;
            entity.PrinterPort = config.PrinterPort;
            entity.UsbDeviceId = usbId;
            entity.UsbDeviceName = usbName;
            entity.PaperWidthMm = paperWidth;
            entity.PrintDensity = density;
            entity.CharacterEncoding = encoding;
            entity.AutoCutEnabled = config.AutoCutEnabled;
            entity.ConnectTimeoutMs = boundedTimeout;
            entity.KickPin = kickBytes[2];
            entity.PulseOnUnits = kickBytes[3];
            entity.PulseOffUnits = kickBytes[4];
            entity.UpdatedBy = auth.AuthorizedBy;
            entity.UpdatedAtUtc = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        var savedSnapshot = new LocalPrinterHardwareConfig
        {
            TerminalId = termId,
            ConnectionType = modeString,
            PrinterIpAddress = normalizedHost,
            PrinterPort = config.PrinterPort,
            UsbDeviceId = usbId,
            UsbDeviceName = usbName
        };

        return new CashDrawerActionResult
        {
            Success = true,
            Message = $"Printer ({modeString}) and cash drawer hardware configuration saved.",
            CommandHex = EscPosCommands.FormatHex(kickBytes),
            Endpoint = ResolveConnection(savedSnapshot).DescribeEndpoint(savedSnapshot)
        };
    }

    public Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverUsbPrintersAsync(CancellationToken cancellationToken = default) =>
        _usbTransport.DiscoverPrintersAsync(cancellationToken);

    public async Task<CashDrawerActionResult> RequestUsbPermissionAsync(string usbDeviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(usbDeviceId))
            return new CashDrawerActionResult { Success = false, Message = "Select a USB printer first." };

        bool granted = await _usbTransport.EnsurePermissionAsync(usbDeviceId, cancellationToken);
        return new CashDrawerActionResult
        {
            Success = granted,
            Message = granted
                ? "USB permission granted for selected thermal printer."
                : "USB permission was not granted or device is disconnected."
        };
    }

    public async Task<PrinterConnectionStatus> GetConnectionStatusAsync(
        LocalPrinterHardwareConfig? overrideConfig = null,
        CancellationToken cancellationToken = default)
    {
        int termId = overrideConfig?.TerminalId > 0
            ? overrideConfig.TerminalId
            : (_session.Terminal?.TerminalId ?? 1);
        var cfg = overrideConfig ?? await GetConfigAsync(termId);
        return await ResolveConnection(cfg).CheckStatusAsync(cfg, cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintReceiptAsync(ReceiptDto receipt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return SendPrintPayloadAsync(
            actionType: receipt.IsReprint ? "PRINT_RECEIPT_REPRINT" : "PRINT_RECEIPT",
            reason: $"Receipt {receipt.InvoiceNumber ?? receipt.OrderNumber}",
            buildPayload: cfg => EscPosReceiptBuilder.BuildReceipt(receipt, cfg, _imageRasterizer),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintPreparationTicketAsync(PreparationTicketDto ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        return SendPrintPayloadAsync(
            actionType: "PRINT_PREP_TICKET",
            reason: $"{ticket.Station} Order {ticket.OrderNumber}",
            buildPayload: cfg => EscPosReceiptBuilder.BuildPreparationTicket(ticket, cfg),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintXReadingAsync(XReadingReportDto report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SendPrintPayloadAsync(
            actionType: "PRINT_X_READING",
            reason: $"X-Reading Shift #{report.ShiftId}",
            buildPayload: cfg => EscPosReceiptBuilder.BuildXReading(report, cfg),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintZReadingAsync(ZReadingReportDto report, bool isReprint = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        return SendPrintPayloadAsync(
            actionType: isReprint ? "PRINT_Z_REPRINT" : "PRINT_Z_READING",
            reason: $"Z-Reading #{report.Id}",
            buildPayload: cfg => EscPosReceiptBuilder.BuildZReading(report, cfg, isReprint),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        return SendPrintPayloadAsync(
            actionType: "PRINT_DECLARATION",
            reason: $"Declaration Shift #{declaration.ShiftId}",
            buildPayload: cfg => EscPosReceiptBuilder.BuildCashierDeclaration(declaration, cfg),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintTextAsync(string text, bool cutPaper = true, CancellationToken cancellationToken = default) =>
        SendPrintPayloadAsync(
            actionType: "PRINT_TEXT",
            reason: "Plain text ESC/POS print",
            buildPayload: cfg => EscPosReceiptBuilder.BuildPlainText(text, cfg, cutPaper),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);

    public Task<CashDrawerActionResult> PrintRawEscPosAsync(ReadOnlyMemory<byte> payload, string description = "Raw ESC/POS", CancellationToken cancellationToken = default)
    {
        byte[] copy = payload.ToArray();
        return SendPrintPayloadAsync(
            actionType: "PRINT_RAW_ESCPOS",
            reason: TrimReason(description) ?? "Raw ESC/POS",
            buildPayload: _ => copy,
            requirePrinterEnabled: false,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> PrintImageAsync(byte[] imageBytes, bool cutPaper = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        return SendPrintPayloadAsync(
            actionType: "PRINT_IMAGE",
            reason: "ESC/POS Raster Image",
            buildPayload: cfg => EscPosReceiptBuilder.BuildSingleBitmap(imageBytes, cfg, _imageRasterizer, cutPaper),
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    public Task<CashDrawerActionResult> CutPaperAsync(CancellationToken cancellationToken = default) =>
        SendPrintPayloadAsync(
            actionType: "CUT_PAPER",
            reason: "ESC/POS Paper Cut",
            buildPayload: EscPosReceiptBuilder.BuildCutPaper,
            requirePrinterEnabled: true,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);

    public async Task<CashDrawerActionResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (!_session.IsAuthenticated || _session.Terminal == null)
            return new CashDrawerActionResult { Success = false, Unauthorized = true, Message = "Terminal session required." };

        int terminalId = _session.Terminal.TerminalId;
        int branchId = _session.Terminal.BranchId;
        var config = await GetConfigAsync(terminalId);
        var conn = ResolveConnection(config);
        var (valid, err) = conn.ValidateConfiguration(config);
        if (!valid)
            return new CashDrawerActionResult { Success = false, Message = err ?? "Invalid printer configuration." };

        var status = await conn.CheckStatusAsync(config, cancellationToken);
        string endpoint = TruncateEndpoint(status.EndpointDisplay);
        await RecordEventAsync(new LocalCashDrawerHardwareEvent
        {
            TerminalId = terminalId,
            BranchId = branchId,
            ActionType = "TEST_CONNECTION",
            RequestedBy = _session.Username ?? "unknown",
            AuthorizedBy = _session.Username,
            AuthorizedRole = _session.Role,
            Reason = $"Connection test ({conn.Mode})",
            TargetEndpoint = endpoint,
            Status = status.IsConnected ? "OPENED" : "FAILED",
            ErrorMessage = status.IsConnected ? null : SanitizeError(status.StatusMessage),
            CreatedAtUtc = DateTime.UtcNow
        });

        return new CashDrawerActionResult
        {
            Success = status.IsConnected,
            Message = status.StatusMessage,
            Endpoint = endpoint
        };
    }

    public Task<CashDrawerActionResult> PrintTestReceiptAsync(CancellationToken cancellationToken = default)
    {
        string terminalName = _session.Terminal?.TerminalName ?? "Terminal";
        return SendPrintPayloadAsync(
            actionType: "TEST_PRINTER",
            reason: "Hardware Test Print Slip",
            buildPayload: cfg =>
            {
                string endpoint = ResolveConnection(cfg).DescribeEndpoint(cfg);
                return EscPosCommands.BuildPrinterTestTicket(
                    terminalName,
                    endpoint,
                    BusinessDayClock.DeviceNow(),
                    cfg.PaperWidthMm,
                    cfg.AutoCutEnabled,
                    cfg.CharacterEncoding,
                    cfg.PrintDensity);
            },
            requirePrinterEnabled: false,
            allowConnectRetry: true,
            cancellationToken: cancellationToken);
    }

    // Legacy ICashDrawerHardwareService alias for PrintTestReceiptAsync
    public Task<CashDrawerActionResult> TestPrinterAsync(CancellationToken cancellationToken = default) =>
        PrintTestReceiptAsync(cancellationToken);

    public Task<CashDrawerActionResult> OpenForCompletedSaleAsync(
        OfflineCheckoutResultDto checkoutResult,
        CancellationToken cancellationToken = default) =>
        OpenCashDrawerForCompletedSaleAsync(checkoutResult, cancellationToken);

    public async Task<CashDrawerActionResult> OpenCashDrawerForCompletedSaleAsync(
        OfflineCheckoutResultDto checkoutResult,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldOpenForCompletedSale(checkoutResult))
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                Message = "Cash drawer not required for this transaction."
            };
        }

        var receipt = checkoutResult.Receipt!;
        string txId = checkoutResult.LocalTransactionId!;
        int terminalId = receipt.TerminalId > 0 ? receipt.TerminalId : (_session.Terminal?.TerminalId ?? 1);

        var config = await GetConfigAsync(terminalId);
        if (!config.CashDrawerEnabled)
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                Message = "Automatic cash drawer opening is disabled in hardware settings."
            };
        }

        string claimKey = GetClaimKey(txId);
        if (!InFlightOrProcessedClaims.TryAdd(claimKey, 0))
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                AlreadyOpened = true,
                Message = $"Cash drawer already processed for transaction {txId}."
            };
        }

        byte[] kickCommand;
        try
        {
            kickCommand = EscPosCommands.BuildCashDrawerKick(config.KickPin, config.PulseOnUnits, config.PulseOffUnits);
        }
        catch
        {
            kickCommand = EscPosCommands.BuildCashDrawerKick();
        }
        string cmdHex = EscPosCommands.FormatHex(kickCommand);

        var conn = ResolveConnection(config);
        var (validEndpoint, endpointErr) = conn.ValidateConfiguration(config);
        string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));

        long eventId;
        await LocalPosDbContext.WriteLock.WaitAsync(cancellationToken);
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            using var dbTx = await db.Database.BeginTransactionAsync(cancellationToken);

            bool alreadyClaimed = await db.CashDrawerHardwareEvents
                .AnyAsync(e => e.TransactionId == txId, cancellationToken);
            if (alreadyClaimed)
            {
                await dbTx.RollbackAsync(cancellationToken);
                return new CashDrawerActionResult
                {
                    Success = true,
                    Skipped = true,
                    AlreadyOpened = true,
                    Message = $"Duplicate cash drawer open blocked for transaction {txId}."
                };
            }

            var claimEvent = new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = receipt.BranchId,
                TransactionId = txId,
                InvoiceNumber = receipt.InvoiceNumber,
                ActionType = "AUTO_SALE_CASH",
                RequestedBy = string.IsNullOrWhiteSpace(receipt.CashierId) ? (_session.Username ?? "cashier") : receipt.CashierId,
                AuthorizedBy = receipt.CashierId,
                AuthorizedRole = _session.Role,
                Reason = $"Cash sale finalized ({receipt.InvoiceNumber})",
                TargetEndpoint = endpoint,
                CommandHex = cmdHex,
                Status = validEndpoint ? "CLAIMED" : "FAILED",
                ErrorMessage = validEndpoint ? null : SanitizeError(endpointErr),
                CreatedAtUtc = DateTime.UtcNow
            };
            db.CashDrawerHardwareEvents.Add(claimEvent);
            await db.SaveChangesAsync(cancellationToken);
            await dbTx.CommitAsync(cancellationToken);
            eventId = claimEvent.EventId;
        }
        catch (DbUpdateException)
        {
            // Unique index on TransactionId guarantees at-most-once execution even under concurrent callbacks.
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                AlreadyOpened = true,
                Message = $"Duplicate cash drawer open prevented for transaction {txId}."
            };
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        if (!validEndpoint)
        {
            string msg = $"Sale saved ({receipt.InvoiceNumber}), but cash drawer could not open: {endpointErr}";
            NotifyWarning(msg);
            return new CashDrawerActionResult
            {
                Success = false,
                Message = msg,
                CommandHex = cmdHex,
                Endpoint = endpoint
            };
        }

        await GlobalHardwareJobGate.WaitAsync(cancellationToken);
        try
        {
            // Never auto-retry drawer kick commands (allowConnectRetry: false) so drawer cannot pop twice.
            await conn.SendBytesAsync(config, kickCommand, allowConnectRetry: false, cancellationToken);
            await UpdateEventStatusAsync(eventId, "OPENED", null);
            return new CashDrawerActionResult
            {
                Success = true,
                Message = $"Cash drawer opened via {endpoint}.",
                CommandHex = cmdHex,
                Endpoint = endpoint
            };
        }
        catch (Exception ex)
        {
            string safeError = SanitizeError(ex.Message);
            await UpdateEventStatusAsync(eventId, "FAILED", safeError);
            LogHardwareFailure("AutoOpenForSale", receipt.InvoiceNumber, endpoint, safeError);
            string warning = $"Sale completed ({receipt.InvoiceNumber}), but cash drawer did not respond at {endpoint}: {safeError}";
            NotifyWarning(warning);
            return new CashDrawerActionResult
            {
                Success = false,
                Message = warning,
                CommandHex = cmdHex,
                Endpoint = endpoint
            };
        }
        finally
        {
            GlobalHardwareJobGate.Release();
        }
    }

    public async Task<LocalReceiptPrintJob?> GetReceiptPrintJobAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId)) return null;
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        return await db.ReceiptPrintJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.TransactionId == transactionId, cancellationToken);
    }

    public async Task<CashDrawerActionResult> AutoPrintReceiptForCompletedSaleAsync(
        OfflineCheckoutResultDto checkoutResult,
        CancellationToken cancellationToken = default)
    {
        if (!ShouldAutoPrintForCompletedSale(checkoutResult))
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                JobStatus = "SKIPPED",
                TransactionId = checkoutResult?.LocalTransactionId,
                Message = "Automatic receipt printing skipped: transaction did not complete as a new sale."
            };
        }

        string txId = checkoutResult.LocalTransactionId!;
        ReceiptDto? savedReceipt;
        using (var verifyDb = new LocalPosDbContext())
        {
            verifyDb.InitializeDatabase();
            savedReceipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(verifyDb, txId, _session, cancellationToken);
        }

        if (savedReceipt == null)
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                JobStatus = "SKIPPED",
                TransactionId = txId,
                Message = "Automatic receipt printing skipped: no finalized paid transaction found in local SQLite."
            };
        }

        int terminalId = savedReceipt.TerminalId > 0 ? savedReceipt.TerminalId : (_session.Terminal?.TerminalId ?? 1);
        var config = await GetConfigAsync(terminalId);
        if (!config.PrinterEnabled)
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                JobStatus = "SKIPPED",
                TransactionId = txId,
                Message = "Direct ESC/POS receipt printing is disabled for this terminal."
            };
        }

        string claimKey = GetClaimKey(txId);
        if (!InFlightAutoPrintClaims.TryAdd(claimKey, 0))
        {
            var existingRamClaim = await GetReceiptPrintJobAsync(txId, cancellationToken);
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                AlreadyPrinted = existingRamClaim?.Status == ReceiptPrintJobStatuses.Printed,
                JobStatus = existingRamClaim?.Status ?? ReceiptPrintJobStatuses.Printing,
                TransactionId = txId,
                Endpoint = existingRamClaim?.TargetEndpoint,
                Message = $"Duplicate automatic print prevented for transaction {txId}."
            };
        }

        var conn = ResolveConnection(config);
        string connType = PrinterConnectionModes.Normalize(config.ConnectionType);
        string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));

        long jobId;
        await LocalPosDbContext.WriteLock.WaitAsync(cancellationToken);
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            using var dbTx = await db.Database.BeginTransactionAsync(cancellationToken);

            var existingJob = await db.ReceiptPrintJobs.AsNoTracking()
                .FirstOrDefaultAsync(j => j.TransactionId == txId, cancellationToken);
            if (existingJob != null)
            {
                await dbTx.RollbackAsync(cancellationToken);
                return new CashDrawerActionResult
                {
                    Success = true,
                    Skipped = true,
                    AlreadyPrinted = existingJob.Status == ReceiptPrintJobStatuses.Printed,
                    JobStatus = existingJob.Status,
                    TransactionId = txId,
                    Endpoint = existingJob.TargetEndpoint,
                    Message = $"Duplicate automatic print prevented for transaction {txId} (status: {existingJob.Status})."
                };
            }

            var now = DateTime.UtcNow;
            var job = new LocalReceiptPrintJob
            {
                TransactionId = txId,
                OrderNumber = savedReceipt.OrderNumber,
                InvoiceNumber = savedReceipt.InvoiceNumber,
                TerminalId = terminalId,
                BranchId = savedReceipt.BranchId,
                ConnectionType = connType,
                TargetEndpoint = endpoint,
                Status = ReceiptPrintJobStatuses.Pending,
                AttemptCount = 0,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.ReceiptPrintJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            await dbTx.CommitAsync(cancellationToken);
            jobId = job.PrintJobId;
        }
        catch (DbUpdateException)
        {
            var existingOnRace = await GetReceiptPrintJobAsync(txId, cancellationToken);
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                AlreadyPrinted = existingOnRace?.Status == ReceiptPrintJobStatuses.Printed,
                JobStatus = existingOnRace?.Status ?? ReceiptPrintJobStatuses.Printing,
                TransactionId = txId,
                Endpoint = existingOnRace?.TargetEndpoint,
                Message = $"Duplicate automatic print blocked for transaction {txId}."
            };
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        return await ExecuteTrackedReceiptPrintJobAsync(jobId, txId, savedReceipt, config, cancellationToken);
    }

    public async Task<CashDrawerActionResult> RetryAutoPrintReceiptAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(transactionId))
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Message = "Transaction ID is required to retry receipt printing."
            };
        }

        string retryKey = GetClaimKey(transactionId);
        if (!InFlightManualPrintRetries.TryAdd(retryKey, 0))
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Skipped = true,
                JobStatus = ReceiptPrintJobStatuses.Printing,
                TransactionId = transactionId,
                Message = "Receipt print retry is already in progress."
            };
        }

        try
        {
            ReceiptDto? savedReceipt;
            using (var verifyDb = new LocalPosDbContext())
            {
                verifyDb.InitializeDatabase();
                savedReceipt = await OfflineCheckoutService.LoadSavedReceiptFromDbAsync(verifyDb, transactionId, _session, cancellationToken);
            }

            if (savedReceipt == null)
            {
                return new CashDrawerActionResult
                {
                    Success = false,
                    TransactionId = transactionId,
                    Message = "Saved paid receipt could not be found in local database."
                };
            }

            int terminalId = savedReceipt.TerminalId > 0 ? savedReceipt.TerminalId : (_session.Terminal?.TerminalId ?? 1);
            var config = await GetConfigAsync(terminalId);
            if (!config.PrinterEnabled)
            {
                return new CashDrawerActionResult
                {
                    Success = false,
                    Skipped = true,
                    JobStatus = "SKIPPED",
                    TransactionId = transactionId,
                    Message = "Direct ESC/POS receipt printer is disabled in settings."
                };
            }

            var conn = ResolveConnection(config);
            string connType = PrinterConnectionModes.Normalize(config.ConnectionType);
            string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));

            long jobId;
            await LocalPosDbContext.WriteLock.WaitAsync(cancellationToken);
            try
            {
                using var db = new LocalPosDbContext();
                db.InitializeDatabase();
                using var dbTx = await db.Database.BeginTransactionAsync(cancellationToken);

                var job = await db.ReceiptPrintJobs.FirstOrDefaultAsync(j => j.TransactionId == transactionId, cancellationToken);
                if (job != null)
                {
                    if (job.Status == ReceiptPrintJobStatuses.Printed)
                    {
                        await dbTx.RollbackAsync(cancellationToken);
                        return new CashDrawerActionResult
                        {
                            Success = true,
                            Skipped = true,
                            AlreadyPrinted = true,
                            JobStatus = ReceiptPrintJobStatuses.Printed,
                            TransactionId = transactionId,
                            Endpoint = job.TargetEndpoint,
                            Message = "Receipt has already been printed for this transaction."
                        };
                    }

                    if (job.Status == ReceiptPrintJobStatuses.Printing)
                    {
                        await dbTx.RollbackAsync(cancellationToken);
                        return new CashDrawerActionResult
                        {
                            Success = false,
                            Skipped = true,
                            JobStatus = ReceiptPrintJobStatuses.Printing,
                            TransactionId = transactionId,
                            Endpoint = job.TargetEndpoint,
                            Message = "Receipt print job is already in progress."
                        };
                    }

                    job.ConnectionType = connType;
                    job.TargetEndpoint = endpoint;
                    job.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    await dbTx.CommitAsync(cancellationToken);
                    jobId = job.PrintJobId;
                }
                else
                {
                    var now = DateTime.UtcNow;
                    var created = new LocalReceiptPrintJob
                    {
                        TransactionId = transactionId,
                        OrderNumber = savedReceipt.OrderNumber,
                        InvoiceNumber = savedReceipt.InvoiceNumber,
                        TerminalId = terminalId,
                        BranchId = savedReceipt.BranchId,
                        ConnectionType = connType,
                        TargetEndpoint = endpoint,
                        Status = ReceiptPrintJobStatuses.Pending,
                        AttemptCount = 0,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now
                    };
                    db.ReceiptPrintJobs.Add(created);
                    await db.SaveChangesAsync(cancellationToken);
                    await dbTx.CommitAsync(cancellationToken);
                    jobId = created.PrintJobId;
                }
            }
            finally
            {
                LocalPosDbContext.WriteLock.Release();
            }

            return await ExecuteTrackedReceiptPrintJobAsync(jobId, transactionId, savedReceipt, config, cancellationToken);
        }
        finally
        {
            InFlightManualPrintRetries.TryRemove(retryKey, out _);
        }
    }

    private async Task<CashDrawerActionResult> ExecuteTrackedReceiptPrintJobAsync(
        long jobId,
        string transactionId,
        ReceiptDto savedReceipt,
        LocalPrinterHardwareConfig config,
        CancellationToken cancellationToken)
    {
        var conn = ResolveConnection(config);
        string connType = PrinterConnectionModes.Normalize(config.ConnectionType);
        string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));
        var (valid, err) = conn.ValidateConfiguration(config);

        if (!valid)
        {
            string safeError = SanitizeError(err ?? "Invalid printer configuration.");
            await UpdateReceiptPrintJobAsync(jobId, ReceiptPrintJobStatuses.Failed, connType, endpoint, incrementAttempt: true, printedAtUtc: null, safeError);
            LogReceiptPrintFailure(transactionId, config.TerminalId, connType, endpoint, safeError);
            string warning = $"Payment successful, but receipt printing failed. Please check the printer. ({safeError})";
            NotifyWarning(warning);
            return new CashDrawerActionResult
            {
                Success = false,
                JobStatus = ReceiptPrintJobStatuses.Failed,
                TransactionId = transactionId,
                Endpoint = endpoint,
                Message = warning
            };
        }

        byte[] payload;
        try
        {
            payload = await Task.Run(() => EscPosReceiptBuilder.BuildReceipt(savedReceipt, config, _imageRasterizer), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            string safeError = SanitizeError(ex.Message);
            await UpdateReceiptPrintJobAsync(jobId, ReceiptPrintJobStatuses.Failed, connType, endpoint, incrementAttempt: true, printedAtUtc: null, safeError)
                .ConfigureAwait(false);
            LogReceiptPrintFailure(transactionId, config.TerminalId, connType, endpoint, safeError);
            string warning = $"Payment successful, but receipt printing failed. Please check the printer. ({safeError})";
            NotifyWarning(warning);
            return new CashDrawerActionResult
            {
                Success = false,
                JobStatus = ReceiptPrintJobStatuses.Failed,
                TransactionId = transactionId,
                Endpoint = endpoint,
                Message = warning
            };
        }

        int hardTimeoutMs = EscPosCommands.ClampTimeoutMs(config.ConnectTimeoutMs) + 4000;
        await GlobalHardwareJobGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UpdateReceiptPrintJobAsync(jobId, ReceiptPrintJobStatuses.Printing, connType, endpoint, incrementAttempt: true, printedAtUtc: null, errorMessage: null)
                .ConfigureAwait(false);

            using var hardTimeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            hardTimeoutCts.CancelAfter(hardTimeoutMs);
            try
            {
                await conn.SendBytesAsync(config, payload, allowConnectRetry: false, hardTimeoutCts.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(hardTimeoutMs), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Printer {endpoint} did not complete printing within {hardTimeoutMs} ms.");
            }

            var printedAt = DateTime.UtcNow;
            await UpdateReceiptPrintJobAsync(jobId, ReceiptPrintJobStatuses.Printed, connType, endpoint, incrementAttempt: false, printedAtUtc: printedAt, errorMessage: null)
                .ConfigureAwait(false);

            return new CashDrawerActionResult
            {
                Success = true,
                JobStatus = ReceiptPrintJobStatuses.Printed,
                TransactionId = transactionId,
                Endpoint = endpoint,
                Message = $"Receipt printed via {endpoint}."
            };
        }
        catch (Exception ex)
        {
            string safeError = SanitizeError(ex.Message);
            await UpdateReceiptPrintJobAsync(jobId, ReceiptPrintJobStatuses.Failed, connType, endpoint, incrementAttempt: false, printedAtUtc: null, safeError)
                .ConfigureAwait(false);
            LogReceiptPrintFailure(transactionId, config.TerminalId, connType, endpoint, safeError);
            string warning = $"Payment successful, but receipt printing failed. Please check the printer. ({safeError})";
            NotifyWarning(warning);
            return new CashDrawerActionResult
            {
                Success = false,
                JobStatus = ReceiptPrintJobStatuses.Failed,
                TransactionId = transactionId,
                Endpoint = endpoint,
                Message = warning
            };
        }
        finally
        {
            GlobalHardwareJobGate.Release();
        }
    }

    public Task<CashDrawerActionResult> ManualOpenAsync(
        string reason,
        string? supervisorPin = null,
        CancellationToken cancellationToken = default) =>
        ManualOpenCashDrawerAsync(reason, supervisorPin, cancellationToken);

    public async Task<CashDrawerActionResult> ManualOpenCashDrawerAsync(
        string reason,
        string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        if (!_session.IsAuthenticated || _session.Terminal == null)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Unauthorized = true,
                Message = "Authentication with an active terminal is required to open the cash drawer."
            };
        }

        int terminalId = _session.Terminal.TerminalId;
        int branchId = _session.Terminal.BranchId;
        string requestedBy = _session.Username ?? "unknown";

        var auth = await AuthorizeSupervisorActionAsync(supervisorPin, allowOfflinePin: true, cancellationToken);
        if (!auth.Authorized)
        {
            await RecordEventAsync(new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = branchId,
                ActionType = "MANUAL_OPEN",
                RequestedBy = requestedBy,
                Reason = TrimReason(reason),
                Status = "UNAUTHORIZED",
                ErrorMessage = SanitizeError(auth.ErrorMessage),
                CreatedAtUtc = DateTime.UtcNow
            });
            return new CashDrawerActionResult
            {
                Success = false,
                Unauthorized = true,
                Message = auth.ErrorMessage ?? "Supervisor authorization is required to manually open the cash drawer."
            };
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 3)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Message = "Enter a meaningful reason (at least 3 characters) to open the cash drawer."
            };
        }

        string cleanReason = TrimReason(reason)!;
        var config = await GetConfigAsync(terminalId);
        if (!config.CashDrawerEnabled)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Message = "Cash drawer is disabled. Enable it in Printer & Cash Drawer Settings first."
            };
        }

        return await SendKickAndAuditAsync(
            terminalId, branchId, "MANUAL_OPEN", requestedBy, auth.AuthorizedBy, auth.AuthorizedRole, cleanReason, config, cancellationToken);
    }

    public async Task<CashDrawerActionResult> TestCashDrawerAsync(
        string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        if (!_session.IsAuthenticated || _session.Terminal == null)
            return new CashDrawerActionResult { Success = false, Unauthorized = true, Message = "Terminal session required." };

        var auth = await AuthorizeSupervisorActionAsync(supervisorPin, allowOfflinePin: true, cancellationToken);
        if (!auth.Authorized)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Unauthorized = true,
                Message = auth.ErrorMessage ?? "Supervisor authorization is required to test the cash drawer."
            };
        }

        int terminalId = _session.Terminal.TerminalId;
        int branchId = _session.Terminal.BranchId;
        var config = await GetConfigAsync(terminalId);
        return await SendKickAndAuditAsync(
            terminalId, branchId, "TEST_DRAWER", _session.Username ?? "unknown", auth.AuthorizedBy, auth.AuthorizedRole, "Hardware Test Drawer Kick", config, cancellationToken);
    }

    public async Task<List<LocalCashDrawerHardwareEvent>> GetRecentEventsAsync(int terminalId, int limit = 30)
    {
        if (terminalId <= 0) terminalId = 1;
        limit = Math.Clamp(limit, 1, 200);
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();
        return await db.CashDrawerHardwareEvents
            .AsNoTracking()
            .Where(e => e.TerminalId == terminalId)
            .OrderByDescending(e => e.EventId)
            .Take(limit)
            .ToListAsync();
    }

    private IPrinterConnection ResolveConnection(LocalPrinterHardwareConfig config) =>
        PrinterConnectionModes.Parse(config.ConnectionType) == PrinterConnectionMode.Usb
            ? _usbConnection
            : _ethernetConnection;

    private async Task<CashDrawerActionResult> SendPrintPayloadAsync(
        string actionType,
        string reason,
        Func<LocalPrinterHardwareConfig, byte[]> buildPayload,
        bool requirePrinterEnabled,
        bool allowConnectRetry,
        CancellationToken cancellationToken)
    {
        if (!_session.IsAuthenticated || _session.Terminal == null)
            return new CashDrawerActionResult { Success = false, Unauthorized = true, Message = "Terminal session required." };

        int terminalId = _session.Terminal.TerminalId;
        int branchId = _session.Terminal.BranchId;
        var config = await GetConfigAsync(terminalId);

        if (requirePrinterEnabled && !config.PrinterEnabled)
        {
            return new CashDrawerActionResult
            {
                Success = true,
                Skipped = true,
                Message = "ESC/POS receipt printer is disabled; using OS print fallback."
            };
        }

        var conn = ResolveConnection(config);
        var (valid, err) = conn.ValidateConfiguration(config);
        string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));
        if (!valid)
        {
            return new CashDrawerActionResult
            {
                Success = false,
                Message = err ?? "Invalid printer configuration.",
                Endpoint = endpoint
            };
        }

        byte[] payload;
        try
        {
            payload = await Task.Run(() => buildPayload(config), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new CashDrawerActionResult { Success = false, Message = ex.Message, Endpoint = endpoint };
        }

        await GlobalHardwareJobGate.WaitAsync(cancellationToken);
        try
        {
            await conn.SendBytesAsync(config, payload, allowConnectRetry, cancellationToken);
            await RecordEventAsync(new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = branchId,
                ActionType = actionType,
                RequestedBy = _session.Username ?? "unknown",
                AuthorizedBy = _session.Username,
                AuthorizedRole = _session.Role,
                Reason = TrimReason(reason),
                TargetEndpoint = endpoint,
                CommandHex = $"{payload.Length}B",
                Status = "OPENED",
                CreatedAtUtc = DateTime.UtcNow
            });
            return new CashDrawerActionResult
            {
                Success = true,
                Message = $"Print job sent via {endpoint}.",
                Endpoint = endpoint
            };
        }
        catch (Exception ex)
        {
            string safeError = SanitizeError(ex.Message);
            await RecordEventAsync(new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = branchId,
                ActionType = actionType,
                RequestedBy = _session.Username ?? "unknown",
                AuthorizedBy = _session.Username,
                AuthorizedRole = _session.Role,
                Reason = TrimReason(reason),
                TargetEndpoint = endpoint,
                CommandHex = $"{payload.Length}B",
                Status = "FAILED",
                ErrorMessage = safeError,
                CreatedAtUtc = DateTime.UtcNow
            });
            LogHardwareFailure(actionType, null, endpoint, safeError);
            string userMsg = $"Printer failed at {endpoint}: {safeError}";
            NotifyWarning(userMsg);
            return new CashDrawerActionResult
            {
                Success = false,
                Message = userMsg,
                Endpoint = endpoint
            };
        }
        finally
        {
            GlobalHardwareJobGate.Release();
        }
    }

    private async Task<CashDrawerActionResult> SendKickAndAuditAsync(
        int terminalId,
        int branchId,
        string actionType,
        string requestedBy,
        string? authorizedBy,
        string? authorizedRole,
        string reason,
        LocalPrinterHardwareConfig config,
        CancellationToken cancellationToken)
    {
        var conn = ResolveConnection(config);
        var (valid, err) = conn.ValidateConfiguration(config);
        if (!valid)
            return new CashDrawerActionResult { Success = false, Message = err ?? "Invalid printer configuration." };

        byte[] kickBytes = EscPosCommands.BuildCashDrawerKick(config.KickPin, config.PulseOnUnits, config.PulseOffUnits);
        string cmdHex = EscPosCommands.FormatHex(kickBytes);
        string endpoint = TruncateEndpoint(conn.DescribeEndpoint(config));

        await GlobalHardwareJobGate.WaitAsync(cancellationToken);
        try
        {
            // Never auto-retry drawer kick commands.
            await conn.SendBytesAsync(config, kickBytes, allowConnectRetry: false, cancellationToken);
            await RecordEventAsync(new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = branchId,
                ActionType = actionType,
                RequestedBy = requestedBy,
                AuthorizedBy = authorizedBy,
                AuthorizedRole = authorizedRole,
                Reason = reason,
                TargetEndpoint = endpoint,
                CommandHex = cmdHex,
                Status = "OPENED",
                CreatedAtUtc = DateTime.UtcNow
            });
            return new CashDrawerActionResult
            {
                Success = true,
                Message = $"Cash drawer pulse ({cmdHex}) sent via {endpoint}.",
                CommandHex = cmdHex,
                Endpoint = endpoint
            };
        }
        catch (Exception ex)
        {
            string safeError = SanitizeError(ex.Message);
            await RecordEventAsync(new LocalCashDrawerHardwareEvent
            {
                TerminalId = terminalId,
                BranchId = branchId,
                ActionType = actionType,
                RequestedBy = requestedBy,
                AuthorizedBy = authorizedBy,
                AuthorizedRole = authorizedRole,
                Reason = reason,
                TargetEndpoint = endpoint,
                CommandHex = cmdHex,
                Status = "FAILED",
                ErrorMessage = safeError,
                CreatedAtUtc = DateTime.UtcNow
            });
            LogHardwareFailure(actionType, null, endpoint, safeError);
            return new CashDrawerActionResult
            {
                Success = false,
                Message = $"Cash drawer failed to open at {endpoint}: {safeError}",
                CommandHex = cmdHex,
                Endpoint = endpoint
            };
        }
        finally
        {
            GlobalHardwareJobGate.Release();
        }
    }

    private async Task<(bool Authorized, string? AuthorizedBy, string? AuthorizedRole, string? ErrorMessage)> AuthorizeSupervisorActionAsync(
        string? supervisorPin,
        bool allowOfflinePin,
        CancellationToken cancellationToken)
    {
        if (!_session.IsAuthenticated || _session.Terminal == null)
            return (false, null, null, "You must be logged in to an active terminal.");

        if (ShiftSession.IsSupervisorRole(_session.Role))
            return (true, _session.Username, _session.Role, null);

        if (string.IsNullOrWhiteSpace(supervisorPin))
            return (false, null, null, "Supervisor PIN is required for this role.");

        if (allowOfflinePin)
        {
            string pinHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(supervisorPin.Trim())));
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var localSup = await db.Cashiers.AsNoTracking()
                .FirstOrDefaultAsync(c => c.PinHash == pinHash, cancellationToken);
            if (localSup != null && ShiftSession.IsSupervisorRole(localSup.Role))
                return (true, localSup.Username, localSup.Role, null);
        }

        if (_userService != null)
        {
            try
            {
                var onlineSup = await _userService.PinLoginAsync(
                    supervisorPin.Trim(),
                    _session.Terminal.TerminalId,
                    _session.Terminal.BranchId);
                if (onlineSup.HasValue && ShiftSession.IsSupervisorRole(onlineSup.Value.Role))
                    return (true, onlineSup.Value.Username, onlineSup.Value.Role, null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Online supervisor PIN verification unavailable; fell back to local cashier cache.");
            }
        }

        return (false, null, null, "Invalid Supervisor / Manager PIN or insufficient role privileges.");
    }

    private static async Task RecordEventAsync(LocalCashDrawerHardwareEvent entry)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            db.CashDrawerHardwareEvents.Add(entry);
            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    private static async Task UpdateEventStatusAsync(long eventId, string status, string? errorMessage)
    {
        if (eventId <= 0) return;
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var entry = await db.CashDrawerHardwareEvents.FirstOrDefaultAsync(e => e.EventId == eventId);
            if (entry != null)
            {
                entry.Status = status;
                entry.ErrorMessage = errorMessage;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    private static async Task UpdateReceiptPrintJobAsync(
        long jobId,
        string status,
        string connectionType,
        string endpoint,
        bool incrementAttempt,
        DateTime? printedAtUtc,
        string? errorMessage)
    {
        if (jobId <= 0) return;
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var job = await db.ReceiptPrintJobs.FirstOrDefaultAsync(j => j.PrintJobId == jobId);
            if (job != null)
            {
                var now = DateTime.UtcNow;
                job.Status = status;
                job.ConnectionType = connectionType;
                job.TargetEndpoint = endpoint;
                if (incrementAttempt)
                {
                    job.AttemptCount++;
                    job.LastAttemptAtUtc = now;
                }
                if (printedAtUtc.HasValue)
                {
                    job.PrintedAtUtc = printedAtUtc.Value;
                }
                job.ErrorMessage = errorMessage;
                job.UpdatedAtUtc = now;
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    private void LogHardwareFailure(string action, string? invoice, string? endpoint, string safeError)
    {
        _logger?.LogWarning("Printer/Drawer failure [{Action}] Invoice={Invoice} Endpoint={Endpoint}: {Error}",
            action, invoice ?? "-", endpoint ?? "-", safeError);
        Debug.WriteLine($"[EscPosPrinter] {action} ({invoice ?? "-"} -> {endpoint ?? "-"}) FAILED: {safeError}");
    }

    private void LogReceiptPrintFailure(string transactionId, int terminalId, string connectionType, string endpoint, string safeError)
    {
        _logger?.LogWarning(
            "Auto receipt print failed. TransactionId={TransactionId} TerminalId={TerminalId} ConnectionType={ConnectionType} Endpoint={Endpoint}: {Error}",
            transactionId, terminalId, connectionType, endpoint, safeError);
        Debug.WriteLine($"[EscPosPrinter] AutoReceiptPrint FAILED TransactionId={transactionId} TerminalId={terminalId} Connection={connectionType} Endpoint={endpoint}: {safeError}");
    }

    private void NotifyWarning(string message)
    {
        try { OnPrinterWarning?.Invoke(message); } catch { }
        try { OnCashierWarning?.Invoke(message); } catch { }
    }

    private static string GetClaimKey(string txId)
    {
        var dbScope = AppContext.GetData("POS.LocalDatabasePath") as string ?? "default";
        return $"{dbScope}|{txId}";
    }

    private static string TruncateEndpoint(string endpoint)
    {
        var clean = string.IsNullOrWhiteSpace(endpoint) ? "unknown" : endpoint.Trim();
        return clean.Length > 80 ? clean[..80] : clean;
    }

    private static string? TrimReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        var clean = reason.Trim();
        return clean.Length > 250 ? clean[..250] : clean;
    }

    private static string SanitizeError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown printer communication error.";
        var clean = message.Trim().ReplaceLineEndings(" ");
        return clean.Length > 380 ? clean[..380] : clean;
    }
}
