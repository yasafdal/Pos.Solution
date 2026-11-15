using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace POS.Client.Services;

/// <summary>
/// ESC/POS byte command builders and printer endpoint validation tailored for network thermal printers
/// such as Xprinter XP-80T (RJ11/RJ12 DK cash drawer kick).
/// </summary>
public static partial class EscPosCommands
{
    public const int DefaultPort = 9100;
    public const int DefaultTimeoutMs = 2000;
    public const int MinTimeoutMs = 500;
    public const int MaxTimeoutMs = 10000;
    public const int DefaultKickPin = 0; // Pin 2 (0x00)
    public const int DefaultPulseOnUnits = 25; // 0x19 -> 25 * 2ms = 50ms
    public const int DefaultPulseOffUnits = 250; // 0xFA -> 250 * 2ms = 500ms
    public const int PaperWidth58Mm = 58;
    public const int PaperWidth80Mm = 80;

    public static readonly byte[] AlignLeft = [0x1B, 0x61, 0x00];
    public static readonly byte[] AlignCenter = [0x1B, 0x61, 0x01];
    public static readonly byte[] AlignRight = [0x1B, 0x61, 0x02];
    public static readonly byte[] BoldOn = [0x1B, 0x45, 0x01];
    public static readonly byte[] BoldOff = [0x1B, 0x45, 0x00];
    public static readonly byte[] SizeNormal = [0x1D, 0x21, 0x00];
    public static readonly byte[] SizeDoubleHeight = [0x1D, 0x21, 0x01];
    public static readonly byte[] SizeDoubleBoth = [0x1D, 0x21, 0x11];

    private static int _codePagesRegistered;

    [GeneratedRegex(@"^[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?)*$")]
    private static partial Regex HostnameRegex();

    public static int NormalizePaperWidthMm(int paperWidthMm) =>
        paperWidthMm == PaperWidth58Mm ? PaperWidth58Mm : PaperWidth80Mm;

    public static int GetColumnsPerLine(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) == PaperWidth58Mm ? 32 : 48;

    public static int GetMaxLogoWidthDots(int paperWidthMm) =>
        NormalizePaperWidthMm(paperWidthMm) == PaperWidth58Mm ? 384 : 512;

    public static string NormalizeDensity(string? density) => density?.Trim().ToUpperInvariant() switch
    {
        "LIGHT" => "Light",
        "DARK" => "Dark",
        _ => "Normal"
    };

    public static string NormalizeEncoding(string? encoding) => encoding?.Trim().ToUpperInvariant() switch
    {
        "PC850" or "CP850" => "PC850",
        "WPC1252" or "CP1252" or "WINDOWS-1252" => "WPC1252",
        "UTF-8" or "UTF8" => "UTF-8",
        _ => "PC437"
    };

    /// <summary>
    /// Builds ESC p m t1 t2 command (1B 70 m t1 t2).
    /// Default parameters yield 1B 70 00 19 FA (Pin 2, 50ms ON, 500ms OFF).
    /// </summary>
    public static byte[] BuildCashDrawerKick(
        int kickPin = DefaultKickPin,
        int pulseOnUnits = DefaultPulseOnUnits,
        int pulseOffUnits = DefaultPulseOffUnits)
    {
        byte pinByte = kickPin switch
        {
            0 or 48 => 0x00,
            1 or 49 => 0x01,
            _ => throw new ArgumentOutOfRangeException(nameof(kickPin), kickPin, "Cash drawer kick pin must be 0 (Pin 2) or 1 (Pin 5).")
        };

        if (pulseOnUnits < 1 || pulseOnUnits > 255)
            throw new ArgumentOutOfRangeException(nameof(pulseOnUnits), pulseOnUnits, "Pulse ON units (t1 * 2ms) must be between 1 and 255.");

        if (pulseOffUnits < 1 || pulseOffUnits > 255)
            throw new ArgumentOutOfRangeException(nameof(pulseOffUnits), pulseOffUnits, "Pulse OFF units (t2 * 2ms) must be between 1 and 255.");

        return [0x1B, 0x70, pinByte, (byte)pulseOnUnits, (byte)pulseOffUnits];
    }

    public static byte[] BuildInitialize() => [0x1B, 0x40];

    public static byte[] BuildFeedLines(int lines = 3) =>
        [0x1B, 0x64, (byte)Math.Clamp(lines, 1, 15)];

    public static byte[] BuildPaperCut(bool feedBeforeCut = true) =>
        feedBeforeCut ? [0x1B, 0x64, 0x03, 0x1D, 0x56, 0x42, 0x00] : [0x1D, 0x56, 0x42, 0x00];

    public static byte[] BuildSelectCodePage(string? encodingName) => NormalizeEncoding(encodingName) switch
    {
        "PC850" => [0x1B, 0x74, 0x02],
        "WPC1252" => [0x1B, 0x74, 0x10],
        "UTF-8" => [],
        _ => [0x1B, 0x74, 0x00] // PC437
    };

    /// <summary>
    /// Standard ESC/POS print density (GS ( K 02 00 31 m).
    /// Returns empty bytes for Normal so hardware default is unchanged.
    /// </summary>
    public static byte[] BuildPrintDensity(string? density) => NormalizeDensity(density) switch
    {
        "Light" => [0x1D, 0x28, 0x4B, 0x02, 0x00, 0x31, 0xFD],
        "Dark" => [0x1D, 0x28, 0x4B, 0x02, 0x00, 0x31, 0x03],
        _ => []
    };

    /// <summary>Builds ESC/POS GS v 0 raster monochrome bitmap packet (1D 76 30 00 xL xH yL yH data).</summary>
    public static byte[] BuildRasterBitImage(EscPosMonochromeBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (bitmap.WidthDots <= 0 || bitmap.WidthDots % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(bitmap), "Raster bitmap width must be a positive multiple of 8.");
        if (bitmap.HeightDots <= 0)
            throw new ArgumentOutOfRangeException(nameof(bitmap), "Raster bitmap height must be positive.");

        int widthBytes = bitmap.WidthDots / 8;
        int expectedLen = widthBytes * bitmap.HeightDots;
        if (bitmap.RasterData.Length != expectedLen)
            throw new ArgumentException($"Expected {expectedLen} raster bytes, got {bitmap.RasterData.Length}.", nameof(bitmap));

        byte[] header =
        [
            0x1D, 0x76, 0x30, 0x00,
            (byte)(widthBytes & 0xFF),
            (byte)((widthBytes >> 8) & 0xFF),
            (byte)(bitmap.HeightDots & 0xFF),
            (byte)((bitmap.HeightDots >> 8) & 0xFF)
        ];

        byte[] packet = new byte[header.Length + bitmap.RasterData.Length];
        Buffer.BlockCopy(header, 0, packet, 0, header.Length);
        Buffer.BlockCopy(bitmap.RasterData, 0, packet, header.Length, bitmap.RasterData.Length);
        return packet;
    }

    public static byte[] EncodeText(string? text, string? encodingName)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        string mode = NormalizeEncoding(encodingName);
        string safeText = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace('—', '-')
            .Replace('–', '-')
            .Replace('·', '|')
            .Replace('’', '\'')
            .Replace('‘', '\'')
            .Replace('“', '"')
            .Replace('”', '"');

        if (mode == "UTF-8")
            return Encoding.UTF8.GetBytes(safeText);

        safeText = safeText.Replace("₱", "PHP ", StringComparison.Ordinal);

        if (Interlocked.CompareExchange(ref _codePagesRegistered, 1, 0) == 0)
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
        }

        try
        {
            int codePage = mode switch
            {
                "PC850" => 850,
                "WPC1252" => 1252,
                _ => 437
            };
            return Encoding.GetEncoding(codePage).GetBytes(safeText);
        }
        catch
        {
            return Encoding.ASCII.GetBytes(safeText);
        }
    }

    public static byte[] BuildPrinterTestTicket(
        string terminalName,
        string endpoint,
        DateTime manilaTimestamp,
        int paperWidthMm = PaperWidth80Mm,
        bool autoCut = true,
        string? encodingName = null,
        string? printDensity = null)
    {
        int cols = GetColumnsPerLine(paperWidthMm);
        string sep = new('-', cols);
        var sb = new StringBuilder();
        sb.Append('\n');
        sb.Append("XPRINTER XP-80T CONNECTION TEST\n");
        sb.Append(sep).Append('\n');
        sb.Append("Terminal : ").Append(string.IsNullOrWhiteSpace(terminalName) ? "POS" : terminalName.Trim()).Append('\n');
        sb.Append("Endpoint : ").Append(endpoint).Append('\n');
        sb.Append("Paper    : ").Append(NormalizePaperWidthMm(paperWidthMm)).Append("mm (").Append(cols).Append(" cols)\n");
        sb.Append("Time     : ").Append(manilaTimestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Status   : READY\n");
        sb.Append(sep).Append("\n\n");

        using var ms = new MemoryStream();
        ms.Write(BuildInitialize());
        ms.Write(BuildPrintDensity(printDensity));
        ms.Write(BuildSelectCodePage(encodingName));
        ms.Write(AlignCenter);
        ms.Write(BoldOn);
        ms.Write(EncodeText("AGRES POS HARDWARE CHECK\n", encodingName));
        ms.Write(BoldOff);
        ms.Write(AlignLeft);
        ms.Write(EncodeText(sb.ToString(), encodingName));
        ms.Write(autoCut ? BuildPaperCut() : BuildFeedLines(4));
        return ms.ToArray();
    }

    public static string FormatHex(ReadOnlySpan<byte> command)
    {
        if (command.IsEmpty) return string.Empty;
        var sb = new StringBuilder(command.Length * 3);
        for (int i = 0; i < command.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(command[i].ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    public static bool TryValidateEndpoint(string? rawHost, int port, out string normalizedHost, out string? error)
    {
        normalizedHost = string.Empty;
        error = null;

        if (port < 1 || port > 65535)
        {
            error = "Printer TCP port must be between 1 and 65535 (default 9100).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawHost))
        {
            error = "Printer IP address is required.";
            return false;
        }

        string candidate = rawHost.Trim();
        if (candidate.Length > 64)
        {
            error = "Printer IP address or hostname is too long (max 64 characters).";
            return false;
        }

        if (IPAddress.TryParse(candidate, out var parsedIp))
        {
            normalizedHost = parsedIp.ToString();
            return true;
        }

        if (candidate.Contains("://", StringComparison.Ordinal) ||
            candidate.Contains('/') ||
            candidate.Contains('\\') ||
            candidate.Contains(':') ||
            !HostnameRegex().IsMatch(candidate))
        {
            error = "Enter a valid IPv4 address (e.g. 192.168.1.100) or LAN hostname.";
            return false;
        }

        normalizedHost = candidate;
        return true;
    }

    public static int ClampTimeoutMs(int timeoutMs) =>
        Math.Clamp(timeoutMs <= 0 ? DefaultTimeoutMs : timeoutMs, MinTimeoutMs, MaxTimeoutMs);
}
