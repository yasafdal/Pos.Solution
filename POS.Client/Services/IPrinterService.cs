using POS.Client.Data;
using POS.Shared.Models;

namespace POS.Client.Services;

public sealed class CashDrawerActionResult
{
    public bool Success { get; init; }
    public bool Skipped { get; init; }
    public bool AlreadyOpened { get; init; }
    public bool AlreadyPrinted { get; init; }
    public bool Unauthorized { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? CommandHex { get; init; }
    public string? Endpoint { get; init; }
    public string? JobStatus { get; init; }
    public string? TransactionId { get; init; }
}

public interface ICashDrawerHardwareService
{
    event Action<string>? OnCashierWarning;
    Task<LocalPrinterHardwareConfig> GetConfigAsync(int terminalId);
    Task<CashDrawerActionResult> SaveConfigAsync(LocalPrinterHardwareConfig config, string? supervisorPin = null, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> OpenForCompletedSaleAsync(OfflineCheckoutResultDto checkoutResult, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> ManualOpenAsync(string reason, string? supervisorPin = null, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> TestCashDrawerAsync(string? supervisorPin = null, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> TestPrinterAsync(CancellationToken cancellationToken = default);
    Task<List<LocalCashDrawerHardwareEvent>> GetRecentEventsAsync(int terminalId, int limit = 30);
}

public enum PrinterConnectionMode
{
    Ethernet = 0,
    Usb = 1
}

public static class PrinterConnectionModes
{
    public const string Ethernet = "Ethernet";
    public const string Usb = "USB";

    public static PrinterConnectionMode Parse(string? value) =>
        string.Equals(value?.Trim(), Usb, StringComparison.OrdinalIgnoreCase)
            ? PrinterConnectionMode.Usb
            : PrinterConnectionMode.Ethernet;

    public static string Normalize(string? value) =>
        Parse(value) == PrinterConnectionMode.Usb ? Usb : Ethernet;
}

public sealed record DiscoveredUsbPrinter(
    string DeviceId,
    string DisplayName,
    int? VendorId,
    int? ProductId,
    string? SerialNumber,
    string TransportKind,
    bool RequiresPermission,
    bool HasPermission,
    bool IsConnected,
    string? StatusNote = null);

public sealed record PrinterConnectionStatus(
    bool IsConfigured,
    bool IsConnected,
    PrinterConnectionMode Mode,
    string EndpointDisplay,
    string StatusMessage,
    bool RequiresPermission = false,
    bool PermissionGranted = true);

public sealed record EscPosMonochromeBitmap(
    int WidthDots,
    int HeightDots,
    byte[] RasterData);

/// <summary>Decodes PNG/JPEG/BMP image data into packed 1-bit MSB raster rows for ESC/POS GS v 0.</summary>
public interface IEscPosImageRasterizer
{
    EscPosMonochromeBitmap? TryRasterizeDataUri(string? dataUri, int maxWidthDots, int maxHeightDots = 180);
    EscPosMonochromeBitmap? TryRasterizeBytes(byte[] imageBytes, int maxWidthDots, int maxHeightDots = 180);
}

/// <summary>Physical transport strategy implemented uniformly for Ethernet TCP/IP and USB printers.</summary>
public interface IPrinterConnection
{
    PrinterConnectionMode Mode { get; }
    string DescribeEndpoint(LocalPrinterHardwareConfig config);
    (bool Valid, string? Error) ValidateConfiguration(LocalPrinterHardwareConfig config);
    Task<PrinterConnectionStatus> CheckStatusAsync(LocalPrinterHardwareConfig config, CancellationToken cancellationToken = default);
    Task SendBytesAsync(
        LocalPrinterHardwareConfig config,
        ReadOnlyMemory<byte> payload,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default);
}

/// <summary>Platform-specific USB thermal printer transport (Windows usbprint/RAW or Android USB Host).</summary>
public interface IUsbPrinterTransport
{
    bool IsSupportedOnPlatform { get; }
    Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken = default);
    Task<bool> EnsurePermissionAsync(string? deviceId, CancellationToken cancellationToken = default);
    Task<PrinterConnectionStatus> CheckStatusAsync(string? deviceId, string? cachedName, int timeoutMs, CancellationToken cancellationToken = default);
    Task SendBytesAsync(
        string? deviceId,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default);
}

/// <summary>Unified ESC/POS printer service supporting USB and Ethernet printing plus RJ11/RJ12 cash drawer control.</summary>
public interface IPrinterService
{
    event Action<string>? OnPrinterWarning;

    Task<LocalPrinterHardwareConfig> GetConfigAsync(int terminalId);
    Task<CashDrawerActionResult> SaveConfigAsync(LocalPrinterHardwareConfig config, string? supervisorPin = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverUsbPrintersAsync(CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> RequestUsbPermissionAsync(string usbDeviceId, CancellationToken cancellationToken = default);
    Task<PrinterConnectionStatus> GetConnectionStatusAsync(LocalPrinterHardwareConfig? overrideConfig = null, CancellationToken cancellationToken = default);
    Task<List<LocalCashDrawerHardwareEvent>> GetRecentEventsAsync(int terminalId, int limit = 30);

    Task<CashDrawerActionResult> PrintReceiptAsync(ReceiptDto receipt, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintPreparationTicketAsync(PreparationTicketDto ticket, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintXReadingAsync(XReadingReportDto report, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintZReadingAsync(ZReadingReportDto report, bool isReprint = false, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintCashierDeclarationAsync(CashierDeclarationReportDto declaration, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintTextAsync(string text, bool cutPaper = true, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintRawEscPosAsync(ReadOnlyMemory<byte> payload, string description = "Raw ESC/POS", CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintImageAsync(byte[] imageBytes, bool cutPaper = false, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> CutPaperAsync(CancellationToken cancellationToken = default);

    Task<CashDrawerActionResult> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> PrintTestReceiptAsync(CancellationToken cancellationToken = default);

    Task<CashDrawerActionResult> OpenCashDrawerForCompletedSaleAsync(OfflineCheckoutResultDto checkoutResult, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> AutoPrintReceiptForCompletedSaleAsync(OfflineCheckoutResultDto checkoutResult, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> RetryAutoPrintReceiptAsync(string transactionId, CancellationToken cancellationToken = default);
    Task<LocalReceiptPrintJob?> GetReceiptPrintJobAsync(string transactionId, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> ManualOpenCashDrawerAsync(string reason, string? supervisorPin = null, CancellationToken cancellationToken = default);
    Task<CashDrawerActionResult> TestCashDrawerAsync(string? supervisorPin = null, CancellationToken cancellationToken = default);
}
