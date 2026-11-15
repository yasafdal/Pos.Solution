#if ANDROID
using System.Globalization;
using System.Text.RegularExpressions;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Usb;
using Android.OS;
using POS.Client.Services;

namespace POS.Client.Platforms.Android;

/// <summary>
/// Android USB Host ESC/POS transport using Android.Hardware.Usb.UsbManager.
/// Handles printer-class discovery, runtime USB permissions, chunked bulk OUT writes, and replug recovery.
/// </summary>
public sealed partial class AndroidUsbPrinterTransport : IUsbPrinterTransport, IDisposable
{
    private const string ActionUsbPermission = "com.companyname.pos.client.USB_PERMISSION";
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    [GeneratedRegex(@"vid=([0-9a-fA-F]{4}).*pid=([0-9a-fA-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidVidPidRegex();

    public bool IsSupportedOnPlatform => true;

    public Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DiscoveredUsbPrinter>>(() =>
        {
            var list = new List<DiscoveredUsbPrinter>();
            var manager = GetUsbManager(out _);
            if (manager?.DeviceList == null)
                return list;

            foreach (var device in manager.DeviceList.Values)
            {
                if (device == null) continue;
                var endpoint = FindPrinterEndpoint(device);
                if (endpoint == null) continue;

                bool hasPermission = SafeHasPermission(manager, device);
                string? serial = null;
                if (hasPermission)
                {
                    try { serial = device.SerialNumber; } catch { }
                }

                string stableId = BuildDeviceId(device, serial);
                string product = !string.IsNullOrWhiteSpace(device.ProductName)
                    ? device.ProductName!
                    : !string.IsNullOrWhiteSpace(device.ManufacturerName)
                        ? $"{device.ManufacturerName} USB Printer"
                        : "USB Thermal Printer";

                string display = $"{product.Trim()} (VID:{device.VendorId:X4} PID:{device.ProductId:X4})";
                bool isPrinterClass = endpoint.Value.Interface.InterfaceClass == UsbClass.Printer;

                list.Add(new DiscoveredUsbPrinter(
                    DeviceId: stableId,
                    DisplayName: display,
                    VendorId: device.VendorId,
                    ProductId: device.ProductId,
                    SerialNumber: serial,
                    TransportKind: isPrinterClass ? "ANDROID_USB_HOST" : "ANDROID_USB_BULK",
                    RequiresPermission: true,
                    HasPermission: hasPermission,
                    IsConnected: true,
                    StatusNote: hasPermission
                        ? "Android USB Host Ready"
                        : "Tap 'Grant USB Permission' to allow printing"));
            }

            return list;
        }, cancellationToken);
    }

    public async Task<bool> EnsurePermissionAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        var manager = GetUsbManager(out var ctx);
        if (manager == null || ctx == null)
            return false;

        var device = FindMatchingDevice(manager, deviceId);
        if (device == null)
            return false;

        if (SafeHasPermission(manager, device))
            return true;

        var permissionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var receiver = new UsbPermissionBroadcastReceiver(device.DeviceName, permissionTcs);
        using var filter = new IntentFilter(ActionUsbPermission);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            ctx.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            ctx.RegisterReceiver(receiver, filter);

        try
        {
            using var intent = new Intent(ActionUsbPermission);
            intent.SetPackage(ctx.PackageName);
            var flags = PendingIntentFlags.UpdateCurrent |
                        (OperatingSystem.IsAndroidVersionAtLeast(31) ? PendingIntentFlags.Mutable : 0);
            using var pendingIntent = PendingIntent.GetBroadcast(ctx, 0, intent, flags);

            manager.RequestPermission(device, pendingIntent);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            using var reg = timeoutCts.Token.Register(() => permissionTcs.TrySetResult(false));

            return await permissionTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            try { ctx.UnregisterReceiver(receiver); } catch { }
        }
    }

    public Task<PrinterConnectionStatus> CheckStatusAsync(
        string? deviceId,
        string? cachedName,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return new PrinterConnectionStatus(
                    false, false, PrinterConnectionMode.Usb, "USB Unconfigured", "Select a USB printer device.");
            }

            var manager = GetUsbManager(out _);
            if (manager == null)
            {
                return new PrinterConnectionStatus(
                    true, false, PrinterConnectionMode.Usb, cachedName ?? "Android USB", "Android UsbManager service unavailable.");
            }

            var device = FindMatchingDevice(manager, deviceId);
            if (device == null)
            {
                return new PrinterConnectionStatus(
                    IsConfigured: true,
                    IsConnected: false,
                    Mode: PrinterConnectionMode.Usb,
                    EndpointDisplay: cachedName ?? deviceId,
                    StatusMessage: "USB printer not attached. Check OTG adapter, cable, and power.");
            }

            var ep = FindPrinterEndpoint(device);
            if (ep == null)
            {
                return new PrinterConnectionStatus(
                    true, false, PrinterConnectionMode.Usb, cachedName ?? device.DeviceName ?? "USB Device",
                    "Connected USB device does not expose a bulk printer OUT endpoint.");
            }

            bool permitted = SafeHasPermission(manager, device);
            string display = !string.IsNullOrWhiteSpace(device.ProductName)
                ? $"{device.ProductName} (VID:{device.VendorId:X4} PID:{device.ProductId:X4})"
                : (cachedName ?? $"USB (VID:{device.VendorId:X4} PID:{device.ProductId:X4})");

            return new PrinterConnectionStatus(
                IsConfigured: true,
                IsConnected: permitted,
                Mode: PrinterConnectionMode.Usb,
                EndpointDisplay: display,
                StatusMessage: permitted
                    ? "Connected via Android USB Host"
                    : "Attached — Android USB permission required",
                RequiresPermission: true,
                PermissionGranted: permitted);
        }, cancellationToken);
    }

    public async Task SendBytesAsync(
        string? deviceId,
        ReadOnlyMemory<byte> payload,
        int timeoutMs,
        bool allowConnectRetry,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new InvalidOperationException("USB printer device is not configured.");
        if (payload.IsEmpty)
            return;

        int boundedTimeout = EscPosCommands.ClampTimeoutMs(timeoutMs);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manager = GetUsbManager(out _)
                ?? throw new PrinterConnectException("Android UsbManager is unavailable on this device.");

            var device = FindMatchingDevice(manager, deviceId)
                ?? throw new PrinterConnectException("USB printer is disconnected. Reconnect OTG/USB cable and check printer power.");

            if (!SafeHasPermission(manager, device))
            {
                bool granted = await EnsurePermissionAsync(deviceId, cancellationToken).ConfigureAwait(false);
                if (!granted)
                    throw new PrinterConnectException("Android USB permission was denied for the thermal printer.");
            }

            var target = FindPrinterEndpoint(device)
                ?? throw new PrinterConnectException("USB printer has no usable bulk OUT endpoint.");

            byte[] bytes = payload.ToArray();
            await Task.Run(() =>
            {
                using UsbDeviceConnection? connection = manager.OpenDevice(device);
                if (connection == null)
                    throw new PrinterConnectException("Unable to open Android USB printer connection. Unplug and reconnect the USB cable.");

                bool claimed = false;
                try
                {
                    claimed = connection.ClaimInterface(target.Interface, true);
                    if (!claimed)
                        throw new PrinterConnectException("Failed to claim USB printer interface (device may be in use by another app).");

                    int chunkSize = Math.Clamp(target.OutEndpoint.MaxPacketSize > 0 ? target.OutEndpoint.MaxPacketSize * 8 : 4096, 512, 16384);
                    int offset = 0;
                    var chunk = new byte[Math.Min(chunkSize, bytes.Length)];

                    while (offset < bytes.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int count = Math.Min(chunkSize, bytes.Length - offset);
                        Buffer.BlockCopy(bytes, offset, chunk, 0, count);

                        int transferred = connection.BulkTransfer(target.OutEndpoint, chunk, count, boundedTimeout);
                        if (transferred < 0)
                        {
                            string errMsg = $"Android USB bulk transfer failed or timed out after {boundedTimeout} ms.";
                            if (offset == 0)
                                throw new PrinterConnectException(errMsg);
                            throw new IOException(errMsg);
                        }
                        if (transferred == 0)
                            throw new IOException("Android USB bulk transfer wrote 0 bytes.");

                        offset += transferred;
                    }
                }
                finally
                {
                    if (claimed)
                    {
                        try { connection.ReleaseInterface(target.Interface); } catch { }
                    }
                    try { connection.Close(); } catch { }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static UsbManager? GetUsbManager(out Context? context)
    {
        context = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity
            ?? global::Android.App.Application.Context;
        return context?.GetSystemService(Context.UsbService) as UsbManager;
    }

    private static bool SafeHasPermission(UsbManager manager, UsbDevice device)
    {
        try { return manager.HasPermission(device); }
        catch { return false; }
    }

    private static string BuildDeviceId(UsbDevice device, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(serial))
            return $"usb:vid={device.VendorId:X4}&pid={device.ProductId:X4}&ser={serial.Trim()}";
        return $"usb:vid={device.VendorId:X4}&pid={device.ProductId:X4}&dev={device.DeviceName}";
    }

    private static UsbDevice? FindMatchingDevice(UsbManager manager, string configuredId)
    {
        var devices = manager.DeviceList?.Values;
        if (devices == null) return null;

        string target = configuredId.Trim();
        foreach (var dev in devices)
        {
            if (dev == null) continue;
            if (string.Equals(dev.DeviceName, target, StringComparison.OrdinalIgnoreCase))
                return dev;

            string? serial = null;
            if (SafeHasPermission(manager, dev))
            {
                try { serial = dev.SerialNumber; } catch { }
            }
            if (string.Equals(BuildDeviceId(dev, serial), target, StringComparison.OrdinalIgnoreCase))
                return dev;
        }

        // Reconnect tolerance: match VID + PID even when Android reassigns /dev/bus/usb/001/00X
        var m = AndroidVidPidRegex().Match(target);
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, null, out int vid) &&
            int.TryParse(m.Groups[2].Value, NumberStyles.HexNumber, null, out int pid))
        {
            return devices.FirstOrDefault(d => d != null && d.VendorId == vid && d.ProductId == pid && FindPrinterEndpoint(d) != null);
        }

        return null;
    }

    private static (UsbInterface Interface, UsbEndpoint OutEndpoint)? FindPrinterEndpoint(UsbDevice device)
    {
        (UsbInterface Interface, UsbEndpoint OutEndpoint)? fallback = null;
        for (int i = 0; i < device.InterfaceCount; i++)
        {
            var intf = device.GetInterface(i);
            if (intf == null) continue;

            for (int e = 0; e < intf.EndpointCount; e++)
            {
                var ep = intf.GetEndpoint(e);
                if (ep == null) continue;
                if (ep.Type == UsbAddressing.XferBulk && ep.Direction == UsbAddressing.Out)
                {
                    if (intf.InterfaceClass == UsbClass.Printer)
                        return (intf, ep);
                    fallback ??= (intf, ep);
                }
            }
        }
        return fallback;
    }

    public void Dispose()
    {
        _ioGate.Dispose();
    }

    private sealed class UsbPermissionBroadcastReceiver : BroadcastReceiver
    {
        private readonly string? _expectedDeviceName;
        private readonly TaskCompletionSource<bool> _tcs;

        public UsbPermissionBroadcastReceiver(string? expectedDeviceName, TaskCompletionSource<bool> tcs)
        {
            _expectedDeviceName = expectedDeviceName;
            _tcs = tcs;
        }

        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent == null || !string.Equals(intent.Action, ActionUsbPermission, StringComparison.Ordinal))
                return;

            UsbDevice? dev = OperatingSystem.IsAndroidVersionAtLeast(33)
                ? intent.GetParcelableExtra(UsbManager.ExtraDevice, Java.Lang.Class.FromType(typeof(UsbDevice))) as UsbDevice
#pragma warning disable CA1422
                : intent.GetParcelableExtra(UsbManager.ExtraDevice) as UsbDevice;
#pragma warning restore CA1422

            if (dev != null && _expectedDeviceName != null && !string.Equals(dev.DeviceName, _expectedDeviceName, StringComparison.Ordinal))
                return;

            bool granted = intent.GetBooleanExtra(UsbManager.ExtraPermissionGranted, false);
            _tcs.TrySetResult(granted);
        }
    }
}

/// <summary>Android BitmapFactory rasterizer for PNG/JPEG/BMP thermal receipt logos (zero extra packages).</summary>
public sealed class AndroidEscPosImageRasterizer : IEscPosImageRasterizer
{
    public EscPosMonochromeBitmap? TryRasterizeDataUri(string? dataUri, int maxWidthDots, int maxHeightDots = 180)
    {
        var bytes = EscPosImagePacker.TryExtractDataUriBytes(dataUri);
        return bytes == null ? null : TryRasterizeBytes(bytes, maxWidthDots, maxHeightDots);
    }

    public EscPosMonochromeBitmap? TryRasterizeBytes(byte[] imageBytes, int maxWidthDots, int maxHeightDots = 180)
    {
        if (imageBytes == null || imageBytes.Length == 0)
            return null;

        var bmpDirect = EscPosImagePacker.TryDecodeUncompressedBmp(imageBytes, maxWidthDots, maxHeightDots);
        if (bmpDirect != null)
            return bmpDirect;

        try
        {
            using var decoded = BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length);
            if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0)
                return null;

            double scale = Math.Min(1.0, Math.Min((double)maxWidthDots / decoded.Width, (double)maxHeightDots / decoded.Height));
            int targetW = Math.Max(1, (int)Math.Round(decoded.Width * scale));
            int targetH = Math.Max(1, (int)Math.Round(decoded.Height * scale));

            using var scaled = (targetW != decoded.Width || targetH != decoded.Height)
                ? Bitmap.CreateScaledBitmap(decoded, targetW, targetH, true)
                : null;
            var source = scaled ?? decoded;

            int[] argb = new int[targetW * targetH];
            source.GetPixels(argb, 0, targetW, 0, 0, targetW, targetH);

            byte[] bgra = new byte[argb.Length * 4];
            for (int i = 0; i < argb.Length; i++)
            {
                int px = argb[i];
                bgra[i * 4] = (byte)(px & 0xFF);         // B
                bgra[i * 4 + 1] = (byte)((px >> 8) & 0xFF);  // G
                bgra[i * 4 + 2] = (byte)((px >> 16) & 0xFF); // R
                bgra[i * 4 + 3] = (byte)((px >> 24) & 0xFF); // A
            }

            return EscPosImagePacker.PackFromPixels(bgra, targetW, targetH, isBgra: true, maxWidthDots, maxHeightDots);
        }
        catch
        {
            return null;
        }
    }
}
#endif
