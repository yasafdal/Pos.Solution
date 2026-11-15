#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using POS.Client.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace POS.Client.Platforms.Windows;

/// <summary>
/// Windows raw USB ESC/POS transport usingSetupAPI (usbprint.sys GUID_DEVINTERFACE_USBPRINT) with
/// overlapped I/O timeouts, plus explicit opt-in winspool.drv RAW queue support (never assuming GDI/V4 drivers support RAW).
/// </summary>
public sealed partial class WindowsUsbPrinterTransport : IUsbPrinterTransport, IDisposable
{
    private const string SpoolerPrefix = "spooler:";
    private static Guid GuidDevInterfaceUsbPrint = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint SpdrpDeviceDesc = 0x00000000;
    private const uint SpdrpMfg = 0x0000000B;
    private const uint SpdrpFriendlyName = 0x0000000C;

    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint ErrorIoPending = 997;
    private const int ErrorSharingViolation = 32;
    private const int ErrorInvalidDatatype = 1804;
    private const uint PrinterEnumLocal = 0x00000002;

    private static readonly string[] IgnoredVirtualPrinters =
    [
        "Microsoft Print to PDF",
        "Microsoft XPS Document Writer",
        "OneNote",
        "Fax",
        "Send To OneNote"
    ];

    private readonly SemaphoreSlim _ioGate = new(1, 1);

    [GeneratedRegex(@"vid_([0-9a-fA-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex VidRegex();

    [GeneratedRegex(@"pid_([0-9a-fA-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PidRegex();

    public bool IsSupportedOnPlatform => true;

    public Task<IReadOnlyList<DiscoveredUsbPrinter>> DiscoverPrintersAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<DiscoveredUsbPrinter>>(() =>
        {
            var results = new List<DiscoveredUsbPrinter>();

            // 1. Direct USB Printer Class interfaces (usbprint.sys)
            results.AddRange(EnumerateDirectUsbPrintDevices());

            // 2. Physical USB/POS Windows Print Spooler queues (opt-in RAW fallback)
            results.AddRange(EnumerateCandidateSpoolerQueues());

            return results;
        }, cancellationToken);
    }

    public Task<bool> EnsurePermissionAsync(string? deviceId, CancellationToken cancellationToken = default) =>
        // Windows desktop Win32 usbprint / spooler does not use runtime Android-style USB prompts.
        Task.FromResult(!string.IsNullOrWhiteSpace(deviceId));

    public async Task<PrinterConnectionStatus> CheckStatusAsync(
        string? deviceId,
        string? cachedName,
        int timeoutMs,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return new PrinterConnectionStatus(
                false, false, PrinterConnectionMode.Usb, "USB Unconfigured", "Select a USB printer device.");
        }

        var discovered = await DiscoverPrintersAsync(cancellationToken).ConfigureAwait(false);
        var match = MatchConfiguredDevice(deviceId, discovered);
        if (match == null)
        {
            return new PrinterConnectionStatus(
                IsConfigured: true,
                IsConnected: false,
                Mode: PrinterConnectionMode.Usb,
                EndpointDisplay: cachedName ?? deviceId,
                StatusMessage: "USB printer not detected. Verify USB cable and printer power.");
        }

        return new PrinterConnectionStatus(
            IsConfigured: true,
            IsConnected: match.IsConnected,
            Mode: PrinterConnectionMode.Usb,
            EndpointDisplay: match.DisplayName,
            StatusMessage: match.IsConnected ? $"Connected ({match.TransportKind})" : (match.StatusNote ?? "Disconnected"),
            RequiresPermission: false,
            PermissionGranted: true);
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
            await Task.Run(() =>
            {
                if (deviceId.StartsWith(SpoolerPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var printerName = deviceId[SpoolerPrefix.Length..].Trim();
                    WriteToRawSpooler(printerName, payload.ToArray());
                }
                else
                {
                    var discovered = EnumerateDirectUsbPrintDevices();
                    var match = MatchConfiguredDevice(deviceId, discovered);
                    string targetPath = match?.DeviceId ?? deviceId.Trim();
                    if (match == null && !targetPath.StartsWith(@"\\.\", StringComparison.Ordinal) && !targetPath.StartsWith(@"\\?\", StringComparison.Ordinal))
                        throw new PrinterConnectException("Configured USB printer is disconnected or not found.");

                    WriteToDirectUsbDevice(targetPath, payload.ToArray(), boundedTimeout, cancellationToken);
                }
            }, cancellationToken).WaitAsync(TimeSpan.FromMilliseconds(boundedTimeout + 2000), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static DiscoveredUsbPrinter? MatchConfiguredDevice(string configuredId, IReadOnlyList<DiscoveredUsbPrinter> candidates)
    {
        string trimmed = configuredId.Trim();
        var exact = candidates.FirstOrDefault(c => string.Equals(c.DeviceId, trimmed, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        // If user moved printer to a different USB hub port, re-match by VID+PID when direct usbprint path changed
        if (TryParseVidPid(trimmed, out int vid, out int pid))
        {
            var vidPidMatch = candidates.FirstOrDefault(c =>
                c.TransportKind == "USBPRINT" && c.VendorId == vid && c.ProductId == pid);
            if (vidPidMatch != null) return vidPidMatch;
        }

        return null;
    }

    private static List<DiscoveredUsbPrinter> EnumerateDirectUsbPrintDevices()
    {
        var list = new List<DiscoveredUsbPrinter>();
        var guid = GuidDevInterfaceUsbPrint;
        IntPtr devInfo = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (devInfo == IntPtr.Zero || devInfo == new IntPtr(-1))
            return list;

        try
        {
            var ifData = new SpDeviceInterfaceData { cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
            for (uint index = 0; SetupDiEnumDeviceInterfaces(devInfo, IntPtr.Zero, ref guid, index, ref ifData); index++)
            {
                uint requiredSize = 0;
                SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, IntPtr.Zero, 0, out requiredSize, IntPtr.Zero);
                if (requiredSize == 0) continue;

                IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
                try
                {
                    // On Unicode builds: cbSize is 8 on 64-bit, 6 on 32-bit.
                    Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 6);
                    var devInfoData = new SpDevInfoData { cbSize = (uint)Marshal.SizeOf<SpDevInfoData>() };

                    if (!SetupDiGetDeviceInterfaceDetail(devInfo, ref ifData, detailBuf, requiredSize, out _, ref devInfoData))
                        continue;

                    string? devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailBuf, 4));
                    if (string.IsNullOrWhiteSpace(devicePath)) continue;

                    string? friendly = GetDeviceRegistryString(devInfo, ref devInfoData, SpdrpFriendlyName)
                        ?? GetDeviceRegistryString(devInfo, ref devInfoData, SpdrpDeviceDesc);
                    string? mfg = GetDeviceRegistryString(devInfo, ref devInfoData, SpdrpMfg);

                    int? vid = null, pid = null;
                    if (TryParseVidPid(devicePath, out int parsedVid, out int parsedPid))
                    {
                        vid = parsedVid;
                        pid = parsedPid;
                    }

                    string label = !string.IsNullOrWhiteSpace(friendly) ? friendly.Trim() : "USB Thermal Printer";
                    if (!string.IsNullOrWhiteSpace(mfg) && !label.Contains(mfg, StringComparison.OrdinalIgnoreCase))
                        label = $"{mfg.Trim()} {label}";
                    if (vid.HasValue && pid.HasValue)
                        label = $"{label} (VID:{vid:X4} PID:{pid:X4})";

                    list.Add(new DiscoveredUsbPrinter(
                        DeviceId: devicePath,
                        DisplayName: label,
                        VendorId: vid,
                        ProductId: pid,
                        SerialNumber: null,
                        TransportKind: "USBPRINT",
                        RequiresPermission: false,
                        HasPermission: true,
                        IsConnected: true,
                        StatusNote: "Direct USB Raw (usbprint.sys)"));
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfo);
        }

        return list;
    }

    private static List<DiscoveredUsbPrinter> EnumerateCandidateSpoolerQueues()
    {
        var list = new List<DiscoveredUsbPrinter>();
        uint needed = 0, returned = 0;
        EnumPrinters(PrinterEnumLocal, null, 2, IntPtr.Zero, 0, ref needed, ref returned);
        if (needed == 0) return list;

        IntPtr buf = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!EnumPrinters(PrinterEnumLocal, null, 2, buf, needed, ref needed, ref returned))
                return list;

            int structSize = Marshal.SizeOf<PrinterInfo2>();
            for (int i = 0; i < returned; i++)
            {
                var info = Marshal.PtrToStructure<PrinterInfo2>(IntPtr.Add(buf, i * structSize));
                string? name = info.pPrinterName;
                string? port = info.pPortName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (IgnoredVirtualPrinters.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (port != null && (port.Contains("PORTPROMPT", StringComparison.OrdinalIgnoreCase) || port.Equals("FILE:", StringComparison.OrdinalIgnoreCase)))
                    continue;

                bool looksUsbOrThermal =
                    (port?.StartsWith("USB", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    name.Contains("POS", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("XP-", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("XPrinter", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Thermal", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("80", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("58", StringComparison.OrdinalIgnoreCase);

                if (!looksUsbOrThermal) continue;

                bool offline = (info.Attributes & 0x00000400) != 0; // PRINTER_ATTRIBUTE_WORK_OFFLINE
                list.Add(new DiscoveredUsbPrinter(
                    DeviceId: SpoolerPrefix + name,
                    DisplayName: $"Spooler Queue: {name} [{port ?? "?"}]",
                    VendorId: null,
                    ProductId: null,
                    SerialNumber: null,
                    TransportKind: "WINDOWS_SPOOLER_RAW",
                    RequiresPermission: false,
                    HasPermission: true,
                    IsConnected: !offline,
                    StatusNote: offline
                        ? "Printer queue marked offline (Requires RAW-capable driver)"
                        : "Windows RAW spooler fallback (Requires RAW-capable V3 driver)"));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return list;
    }

    private static void WriteToDirectUsbDevice(string devicePath, byte[] payload, int timeoutMs, CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = CreateFile(
            devicePath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ErrorSharingViolation)
                throw new PrinterConnectException($"USB printer is busy or locked by the Windows Print Spooler (Win32 error {err}). Pause pending jobs or select the Spooler Queue entry if a vendor RAW driver is installed.");
            if (err == 2 || err == 3 || err == 1167) // FILE_NOT_FOUND / PATH_NOT_FOUND / DEVICE_NOT_CONNECTED
                throw new PrinterConnectException($"USB printer is disconnected or powered off (Win32 error {err}).");
            throw new PrinterConnectException($"Cannot open USB printer '{devicePath}': {new Win32Exception(err).Message}");
        }

        using var doneEvent = new ManualResetEvent(false);
        IntPtr overlappedPtr = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>());
        GCHandle pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
        bool ioStarted = false;

        try
        {
            var ov = new NativeOverlapped
            {
                EventHandle = doneEvent.SafeWaitHandle.DangerousGetHandle()
            };
            Marshal.StructureToPtr(ov, overlappedPtr, false);

            bool completedSync = WriteFile(handle, payload, (uint)payload.Length, out uint written, overlappedPtr);
            if (!completedSync)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ErrorIoPending)
                    throw new IOException($"Direct USB write failed (Win32 error {err}): {new Win32Exception(err).Message}");

                ioStarted = true;
                int signalled = WaitHandle.WaitAny([doneEvent, cancellationToken.WaitHandle], timeoutMs);
                if (signalled == WaitHandle.WaitTimeout)
                {
                    CancelIoEx(handle, overlappedPtr);
                    GetOverlappedResult(handle, overlappedPtr, out _, true);
                    throw new TimeoutException($"Direct USB printer write timed out after {timeoutMs} ms.");
                }
                if (signalled == 1)
                {
                    CancelIoEx(handle, overlappedPtr);
                    GetOverlappedResult(handle, overlappedPtr, out _, true);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                if (!GetOverlappedResult(handle, overlappedPtr, out written, false))
                {
                    int ovErr = Marshal.GetLastWin32Error();
                    throw new IOException($"Direct USB transfer did not complete (Win32 error {ovErr}): {new Win32Exception(ovErr).Message}");
                }
            }

            if (written != payload.Length)
                throw new IOException($"Incomplete direct USB write: sent {written} of {payload.Length} bytes.");
        }
        finally
        {
            if (ioStarted)
            {
                CancelIoEx(handle, overlappedPtr);
            }
            if (pinned.IsAllocated) pinned.Free();
            Marshal.FreeHGlobal(overlappedPtr);
        }
    }

    private static void WriteToRawSpooler(string printerName, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            throw new PrinterConnectException("Windows printer queue name is required.");

        if (!OpenPrinter(printerName, out IntPtr printerHandle, IntPtr.Zero) || printerHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            throw new PrinterConnectException($"Unable to open Windows printer '{printerName}' (Win32 error {err}): {new Win32Exception(err).Message}");
        }

        try
        {
            var docInfo = new DocInfo1
            {
                pDocName = "POS ESC/POS Job",
                pOutputFile = null,
                pDataType = "RAW"
            };

            int jobId = StartDocPrinter(printerHandle, 1, ref docInfo);
            if (jobId == 0)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ErrorInvalidDatatype)
                {
                    throw new PrinterConnectException(
                        $"Installed Windows driver for '{printerName}' rejected RAW ESC/POS pass-through (ERROR_INVALID_DATATYPE). Select the Direct USB (usbprint.sys) device instead.");
                }
                throw new PrinterConnectException($"Failed to start RAW print job on '{printerName}' (Win32 error {err}): {new Win32Exception(err).Message}");
            }

            try
            {
                if (!StartPagePrinter(printerHandle))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException($"StartPagePrinter failed on '{printerName}' (Win32 error {err}).");
                }

                try
                {
                    IntPtr unmanagedBytes = Marshal.AllocCoTaskMem(payload.Length);
                    try
                    {
                        Marshal.Copy(payload, 0, unmanagedBytes, payload.Length);
                        if (!WritePrinter(printerHandle, unmanagedBytes, payload.Length, out int written) || written != payload.Length)
                        {
                            int err = Marshal.GetLastWin32Error();
                            throw new IOException($"WritePrinter sent {written}/{payload.Length} bytes to '{printerName}' (Win32 error {err}).");
                        }
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(unmanagedBytes);
                    }
                }
                finally
                {
                    EndPagePrinter(printerHandle);
                }
            }
            finally
            {
                EndDocPrinter(printerHandle);
            }
        }
        finally
        {
            ClosePrinter(printerHandle);
        }
    }

    private static string? GetDeviceRegistryString(IntPtr devInfo, ref SpDevInfoData devInfoData, uint property)
    {
        SetupDiGetDeviceRegistryProperty(devInfo, ref devInfoData, property, out _, IntPtr.Zero, 0, out uint reqSize);
        if (reqSize <= 2) return null;

        IntPtr buf = Marshal.AllocHGlobal((int)reqSize);
        try
        {
            if (SetupDiGetDeviceRegistryProperty(devInfo, ref devInfoData, property, out _, buf, reqSize, out _))
                return Marshal.PtrToStringUni(buf);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static bool TryParseVidPid(string text, out int vid, out int pid)
    {
        vid = 0;
        pid = 0;
        var v = VidRegex().Match(text);
        var p = PidRegex().Match(text);
        if (!v.Success || !p.Success) return false;
        return int.TryParse(v.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out vid) &&
               int.TryParse(p.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    public void Dispose()
    {
        _ioGate.Dispose();
    }

    #region Win32 Interop

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo1
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDataType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo2
    {
        public string? pServerName;
        public string? pPrinterName;
        public string? pShareName;
        public string? pPortName;
        public string? pDriverName;
        public string? pComment;
        public string? pLocation;
        public IntPtr pDevMode;
        public string? pSepFile;
        public string? pPrintProcessor;
        public string? pDatatype;
        public string? pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr devInfoSet, ref SpDevInfoData deviceInfoData, uint property, out uint propertyRegDataType, IntPtr propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle hFile, IntPtr lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, ref uint pcbNeeded, ref uint pcReturned);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DocInfo1 pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    #endregion
}

/// <summary>Windows WinRT BitmapDecoder rasterizer for PNG/JPEG/BMP thermal receipt logos (zero extra packages).</summary>
public sealed class WindowsEscPosImageRasterizer : IEscPosImageRasterizer
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
            // Run WinRT decoding off any UI SynchronizationContext so synchronous callers never deadlock.
            return Task.Run(() => DecodeWithWinRtAsync(imageBytes, maxWidthDots, maxHeightDots))
                .WaitAsync(TimeSpan.FromSeconds(4))
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            return null;
        }
    }

    private static async Task<EscPosMonochromeBitmap?> DecodeWithWinRtAsync(byte[] imageBytes, int maxWidthDots, int maxHeightDots)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(imageBytes);
            await writer.StoreAsync().AsTask().ConfigureAwait(false);
            await writer.FlushAsync().AsTask().ConfigureAwait(false);
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        uint srcW = decoder.PixelWidth;
        uint srcH = decoder.PixelHeight;
        if (srcW == 0 || srcH == 0) return null;

        double scale = Math.Min(1.0, Math.Min((double)maxWidthDots / srcW, (double)maxHeightDots / srcH));
        uint targetW = Math.Max(1u, (uint)Math.Round(srcW * scale));
        uint targetH = Math.Max(1u, (uint)Math.Round(srcH * scale));

        var transform = new BitmapTransform
        {
            ScaledWidth = targetW,
            ScaledHeight = targetH,
            InterpolationMode = BitmapInterpolationMode.Linear
        };

        var pixelData = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);

        byte[] bgra = pixelData.DetachPixelData();
        return EscPosImagePacker.PackFromPixels(bgra, (int)targetW, (int)targetH, isBgra: true, maxWidthDots, maxHeightDots);
    }
}
#endif
