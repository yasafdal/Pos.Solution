using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Helpers;

namespace POS.Api.Services;

public static class ReceiptLogoStorage
{
    private const string LogosPrefix = "/uploads/logos/";
    private static readonly ConcurrentDictionary<string, string> MemoryDataUriCache = new(StringComparer.OrdinalIgnoreCase);
    private static string? _configuredWebRootPath;
    private static volatile bool _branchColumnsEnsured;

    public static async Task EnsureBranchColumnsAsync(PosDbContext context)
    {
        if (_branchColumnsEnsured || !context.Database.IsNpgsql())
            return;

        try
        {
            await context.Database.ExecuteSqlRawAsync(@"
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUrl"" VARCHAR(255) NULL;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ReceiptLogoUpdatedAt"" TIMESTAMP WITHOUT TIME ZONE NULL;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""VatDisplay"" BOOLEAN NOT NULL DEFAULT TRUE;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""ShowCustomerDetailsOnReceipt"" BOOLEAN NOT NULL DEFAULT TRUE;
                ALTER TABLE ""Branches"" ADD COLUMN IF NOT EXISTS ""BranchTagline"" VARCHAR(200) NULL;
            ");
            _branchColumnsEnsured = true;
        }
        catch
        {
            // Non-fatal if database user has read-only permissions and columns already exist
        }
    }

    public static void ConfigureWebRoot(string? webRootPath, string? contentRootPath = null)
    {
        if (!string.IsNullOrWhiteSpace(webRootPath))
        {
            _configuredWebRootPath = webRootPath;
        }
        else if (!string.IsNullOrWhiteSpace(contentRootPath))
        {
            _configuredWebRootPath = Path.Combine(contentRootPath, "wwwroot");
        }
    }

    public static string ResolveLogosDirectory(string? webRootPath = null, string? contentRootPath = null)
    {
        var root = webRootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = !string.IsNullOrWhiteSpace(contentRootPath)
                ? Path.Combine(contentRootPath, "wwwroot")
                : (_configuredWebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"));
        }

        _configuredWebRootPath = root;
        var dir = Path.Combine(root, "uploads", "logos");
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    public static bool TryResolveSafeFilePath(string? receiptLogoUrl, out string safeFileName, out string fullPath, string? webRootPath = null, string? contentRootPath = null)
    {
        safeFileName = string.Empty;
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(receiptLogoUrl))
            return false;

        var normalized = receiptLogoUrl.Trim().Replace('\\', '/');
        if (!normalized.StartsWith(LogosPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidateName = normalized.Substring(LogosPrefix.Length);
        if (string.IsNullOrWhiteSpace(candidateName) ||
            candidateName.IndexOf('/') >= 0 ||
            candidateName.IndexOf('\\') >= 0 ||
            candidateName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        safeFileName = Path.GetFileName(candidateName);
        if (!string.Equals(safeFileName, candidateName, StringComparison.Ordinal))
            return false;

        var ext = Path.GetExtension(safeFileName).ToLowerInvariant();
        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
            return false;

        var logosDir = ResolveLogosDirectory(webRootPath, contentRootPath);
        var combined = Path.GetFullPath(Path.Combine(logosDir, safeFileName));
        var normalizedLogosDir = Path.GetFullPath(logosDir);
        if (!combined.StartsWith(normalizedLogosDir, StringComparison.OrdinalIgnoreCase))
            return false;

        fullPath = combined;
        return true;
    }

    public static void CacheDataUri(string receiptLogoUrl, string dataUri)
    {
        if (!string.IsNullOrWhiteSpace(receiptLogoUrl) && !string.IsNullOrWhiteSpace(dataUri))
        {
            MemoryDataUriCache[receiptLogoUrl.Trim()] = dataUri;
        }
    }

    public static void EvictCache(string? receiptLogoUrl)
    {
        if (!string.IsNullOrWhiteSpace(receiptLogoUrl))
        {
            MemoryDataUriCache.TryRemove(receiptLogoUrl.Trim(), out _);
        }
    }

    public static string? GetDataUri(string? receiptLogoUrl, string? webRootPath = null, string? contentRootPath = null)
    {
        if (string.IsNullOrWhiteSpace(receiptLogoUrl))
            return null;

        var key = receiptLogoUrl.Trim();
        if (MemoryDataUriCache.TryGetValue(key, out var cached) && ReceiptLogoValidator.IsSafeDataUri(cached))
        {
            return cached;
        }

        if (!TryResolveSafeFilePath(key, out _, out var fullPath, webRootPath, contentRootPath))
            return null;

        try
        {
            if (!File.Exists(fullPath))
                return null;

            var bytes = File.ReadAllBytes(fullPath);
            var validation = ReceiptLogoValidator.Validate(bytes, fullPath);
            if (!validation.IsValid)
                return null;

            var dataUri = ReceiptLogoValidator.ToDataUri(bytes, validation.MimeType);
            MemoryDataUriCache[key] = dataUri;
            return dataUri;
        }
        catch
        {
            return null;
        }
    }

    public static void TryDeleteLogoFile(string? receiptLogoUrl, string? webRootPath = null, string? contentRootPath = null)
    {
        if (string.IsNullOrWhiteSpace(receiptLogoUrl))
            return;

        EvictCache(receiptLogoUrl);

        if (!TryResolveSafeFilePath(receiptLogoUrl, out _, out var fullPath, webRootPath, contentRootPath))
            return;

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }
        catch
        {
            // Non-fatal cleanup
        }
    }
}
