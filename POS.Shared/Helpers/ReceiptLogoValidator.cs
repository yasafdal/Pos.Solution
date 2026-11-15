using System;

namespace POS.Shared.Helpers;

public sealed record ReceiptLogoValidationResult(
    bool IsValid,
    string? ErrorMessage,
    string MimeType,
    string Extension,
    int Width,
    int Height);

public static class ReceiptLogoValidator
{
    public const int MaxFileSizeBytes = 2 * 1024 * 1024; // 2 MB
    public const int MaxDimensionPx = 2048;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    public static ReceiptLogoValidationResult Validate(byte[]? fileBytes, string? fileName = null)
    {
        if (fileBytes == null || fileBytes.Length == 0)
        {
            return Invalid("No image file was provided or the file is empty.");
        }

        if (fileBytes.Length > MaxFileSizeBytes)
        {
            return Invalid("Logo file size exceeds the 2 MB maximum limit.");
        }

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            if (!string.IsNullOrEmpty(ext) && ext != ".png" && ext != ".jpg" && ext != ".jpeg")
            {
                return Invalid("Unsupported file extension. Only PNG (.png) and JPEG (.jpg, .jpeg) images are accepted.");
            }
        }

        if (TryReadPngDimensions(fileBytes, out int pngWidth, out int pngHeight))
        {
            if (pngWidth <= 0 || pngHeight <= 0)
            {
                return Invalid("Corrupted PNG image header: invalid dimensions.");
            }
            if (pngWidth > MaxDimensionPx || pngHeight > MaxDimensionPx)
            {
                return Invalid($"Logo dimensions ({pngWidth}x{pngHeight}px) exceed the maximum allowed {MaxDimensionPx}x{MaxDimensionPx}px.");
            }
            return new ReceiptLogoValidationResult(true, null, "image/png", ".png", pngWidth, pngHeight);
        }

        if (TryReadJpegDimensions(fileBytes, out int jpgWidth, out int jpgHeight))
        {
            if (jpgWidth <= 0 || jpgHeight <= 0)
            {
                return Invalid("Corrupted JPEG image header: invalid dimensions.");
            }
            if (jpgWidth > MaxDimensionPx || jpgHeight > MaxDimensionPx)
            {
                return Invalid($"Logo dimensions ({jpgWidth}x{jpgHeight}px) exceed the maximum allowed {MaxDimensionPx}x{MaxDimensionPx}px.");
            }
            return new ReceiptLogoValidationResult(true, null, "image/jpeg", ".jpg", jpgWidth, jpgHeight);
        }

        return Invalid("Invalid or corrupted image content. Please upload a valid PNG or JPEG image.");
    }

    public static string ToDataUri(byte[] fileBytes, string mimeType)
    {
        return $"data:{mimeType};base64,{Convert.ToBase64String(fileBytes)}";
    }

    public static bool IsSafeDataUri(string? dataUri)
    {
        if (string.IsNullOrWhiteSpace(dataUri))
            return false;

        var trimmed = dataUri.Trim();
        int commaIndex;
        if (trimmed.StartsWith("data:image/png;base64,", StringComparison.OrdinalIgnoreCase))
        {
            commaIndex = "data:image/png;base64,".Length;
        }
        else if (trimmed.StartsWith("data:image/jpeg;base64,", StringComparison.OrdinalIgnoreCase))
        {
            commaIndex = "data:image/jpeg;base64,".Length;
        }
        else if (trimmed.StartsWith("data:image/jpg;base64,", StringComparison.OrdinalIgnoreCase))
        {
            commaIndex = "data:image/jpg;base64,".Length;
        }
        else
        {
            return false;
        }

        if (trimmed.Length <= commaIndex)
            return false;

        for (int i = commaIndex; i < trimmed.Length; i++)
        {
            char c = trimmed[i];
            bool ok = (c >= 'A' && c <= 'Z') ||
                      (c >= 'a' && c <= 'z') ||
                      (c >= '0' && c <= '9') ||
                      c == '+' || c == '/' || c == '=' ||
                      c == '\r' || c == '\n';
            if (!ok) return false;
        }

        return true;
    }

    private static ReceiptLogoValidationResult Invalid(string message) =>
        new(false, message, string.Empty, string.Empty, 0, 0);

    private static bool TryReadPngDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (bytes.Length < 24)
            return false;

        for (int i = 0; i < PngSignature.Length; i++)
        {
            if (bytes[i] != PngSignature[i])
                return false;
        }

        // Verify IHDR chunk type at bytes 12..15 ('I', 'H', 'D', 'R')
        if (bytes[12] != 0x49 || bytes[13] != 0x48 || bytes[14] != 0x44 || bytes[15] != 0x52)
            return false;

        width = ReadBigEndianInt32(bytes, 16);
        height = ReadBigEndianInt32(bytes, 20);
        return width > 0 && height > 0;
    }

    private static bool TryReadJpegDimensions(byte[] bytes, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (bytes.Length < 11)
            return false;

        // SOI marker FF D8 FF
        if (bytes[0] != 0xFF || bytes[1] != 0xD8 || bytes[2] != 0xFF)
            return false;

        int index = 2;
        while (index + 4 < bytes.Length)
        {
            if (bytes[index] != 0xFF)
            {
                index++;
                continue;
            }

            // Skip fill 0xFF bytes
            while (index < bytes.Length && bytes[index] == 0xFF)
            {
                index++;
            }

            if (index >= bytes.Length)
                break;

            byte marker = bytes[index];
            index++;

            // Standalone markers without length
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
                continue;

            // EOI or SOS reached before SOF
            if (marker == 0xD9 || marker == 0xDA)
                break;

            if (index + 1 >= bytes.Length)
                break;

            int segmentLength = (bytes[index] << 8) | bytes[index + 1];
            if (segmentLength < 2 || index + segmentLength > bytes.Length)
                return false;

            // SOF markers: C0..CF except C4 (DHT), C8 (JPG), CC (DAC)
            bool isSof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isSof)
            {
                if (segmentLength < 7 || index + 6 >= bytes.Length)
                    return false;

                height = (bytes[index + 3] << 8) | bytes[index + 4];
                width = (bytes[index + 5] << 8) | bytes[index + 6];
                return width > 0 && height > 0;
            }

            index += segmentLength;
        }

        return false;
    }

    private static int ReadBigEndianInt32(byte[] bytes, int offset)
    {
        return (bytes[offset] << 24) |
               (bytes[offset + 1] << 16) |
               (bytes[offset + 2] << 8) |
               bytes[offset + 3];
    }
}
