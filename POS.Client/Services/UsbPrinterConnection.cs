using POS.Client.Data;

namespace POS.Client.Services;

/// <summary>Unified USB ESC/POS connection delegating device I/O to the active platform's IUsbPrinterTransport.</summary>
public sealed class UsbPrinterConnection : IPrinterConnection
{
    private readonly IUsbPrinterTransport _usbTransport;

    public UsbPrinterConnection(IUsbPrinterTransport usbTransport)
    {
        _usbTransport = usbTransport ?? throw new ArgumentNullException(nameof(usbTransport));
    }

    public PrinterConnectionMode Mode => PrinterConnectionMode.Usb;

    public string DescribeEndpoint(LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!string.IsNullOrWhiteSpace(config.UsbDeviceName))
            return $"USB: {config.UsbDeviceName.Trim()}";
        if (!string.IsNullOrWhiteSpace(config.UsbDeviceId))
        {
            var id = config.UsbDeviceId.Trim();
            return id.Length > 48 ? $"USB: {id[..48]}..." : $"USB: {id}";
        }
        return "USB (unconfigured)";
    }

    public (bool Valid, string? Error) ValidateConfiguration(LocalPrinterHardwareConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_usbTransport.IsSupportedOnPlatform)
            return (false, "Direct USB printing is only supported on Windows and Android platforms.");
        if (string.IsNullOrWhiteSpace(config.UsbDeviceId))
            return (false, "Select a USB printer in Printer & Cash Drawer Settings first.");
        return (true, null);
    }

    public async Task<PrinterConnectionStatus> CheckStatusAsync(LocalPrinterHardwareConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!_usbTransport.IsSupportedOnPlatform)
        {
            return new PrinterConnectionStatus(
                IsConfigured: false,
                IsConnected: false,
                Mode: PrinterConnectionMode.Usb,
                EndpointDisplay: "USB Unsupported",
                StatusMessage: "Direct USB ESC/POS printing is not supported on this OS.");
        }

        if (string.IsNullOrWhiteSpace(config.UsbDeviceId))
        {
            return new PrinterConnectionStatus(
                IsConfigured: false,
                IsConnected: false,
                Mode: PrinterConnectionMode.Usb,
                EndpointDisplay: "USB Unconfigured",
                StatusMessage: "No USB printer selected. Plug in printer and click Discover USB.");
        }

        return await _usbTransport.CheckStatusAsync(
            config.UsbDeviceId,
            config.UsbDeviceName,
            config.ConnectTimeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SendBytesAsync(
        LocalPrinterHardwareConfig config,
        ReadOnlyMemory<byte> payload,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var (valid, err) = ValidateConfiguration(config);
        if (!valid)
            throw new InvalidOperationException(err ?? "USB printer is not configured.");

        int maxAttempts = allowConnectRetry ? 2 : 1;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await _usbTransport.SendBytesAsync(
                    config.UsbDeviceId,
                    payload,
                    EscPosCommands.ClampTimeoutMs(config.ConnectTimeoutMs),
                    allowConnectRetry,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PrinterConnectException) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>Fallback USB transport for platforms without raw USB host access (iOS / MacCatalyst).</summary>
public sealed class UnsupportedUsbPrinterTransport : IUsbPrinterTransport
{
    public bool IsSupportedOnPlatform => false;

    public Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DiscoveredUsbPrinter>>(Array.Empty<DiscoveredUsbPrinter>());

    public Task<bool> EnsurePermissionAsync(string? deviceId, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<PrinterConnectionStatus> CheckStatusAsync(string? deviceId, string? cachedName, int timeoutMs, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PrinterConnectionStatus(
            IsConfigured: !string.IsNullOrWhiteSpace(deviceId),
            IsConnected: false,
            Mode: PrinterConnectionMode.Usb,
            EndpointDisplay: cachedName ?? "USB Unsupported",
            StatusMessage: "USB ESC/POS printing is not supported on this platform."));

    public Task SendBytesAsync(
        string? deviceId,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default) =>
        throw new PlatformNotSupportedException("USB ESC/POS printing is not supported on this platform.");
}
