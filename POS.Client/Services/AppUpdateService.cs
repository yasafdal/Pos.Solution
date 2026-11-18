using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;

namespace POS.Client.Services;

public class AppUpdateService
{
    public const string ExpectedPackageId = "com.agres.pos.client";

    private readonly HttpClient _httpClient;
    private readonly IOfflineTransactionSyncService? _transactionSync;

    public AppUpdateService(HttpClient httpClient, IOfflineTransactionSyncService? transactionSync = null)
    {
        _httpClient = httpClient;
        _transactionSync = transactionSync;
    }

    public static (bool IsValid, string? ErrorMessage) ValidateReleaseManifest(
        AppReleaseManifestDto? manifest,
        int currentVersionCode,
        string expectedPackageId = ExpectedPackageId)
    {
        if (manifest == null)
            return (false, "Release manifest is missing.");

        if (!string.Equals(manifest.PackageId?.Trim(), expectedPackageId, StringComparison.Ordinal))
            return (false, $"Package ID mismatch: expected '{expectedPackageId}', got '{manifest.PackageId}'.");

        if (manifest.VersionCode <= currentVersionCode)
            return (false, $"Release versionCode ({manifest.VersionCode}) is not newer than installed versionCode ({currentVersionCode}). Downgrades are blocked.");

        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Trim().Length != 64)
            return (false, "Release manifest SHA-256 digest is missing or invalid.");

        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
            !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Release download URL must use HTTPS.");
        }

        if (!AppReleaseManifestDto.VerifyManifestSignature(manifest))
            return (false, "Release manifest ECDSA publisher signature verification failed.");

        return (true, null);
    }

    public static (bool IsValid, string? ErrorMessage) VerifyDownloadedApkFile(
        string apkFilePath,
        AppReleaseManifestDto manifest)
    {
        if (string.IsNullOrWhiteSpace(apkFilePath) || !File.Exists(apkFilePath))
            return (false, "Downloaded APK file does not exist.");

        var fileInfo = new FileInfo(apkFilePath);
        if (fileInfo.Length <= 0)
            return (false, "Downloaded APK file is empty.");

        string actualSha256;
        using (var fs = new FileStream(apkFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var hashBytes = SHA256.HashData(fs);
            actualSha256 = Convert.ToHexString(hashBytes);
        }

        var expectedSha256 = (manifest.Sha256 ?? string.Empty).Trim().ToUpperInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(actualSha256),
                System.Text.Encoding.ASCII.GetBytes(expectedSha256)))
        {
            return (false, $"APK SHA-256 integrity mismatch (expected {expectedSha256}, actual {actualSha256}).");
        }

#if ANDROID
        var archiveCheck = VerifyAndroidArchiveIdentity(apkFilePath, manifest);
        if (!archiveCheck.IsValid)
            return archiveCheck;
#endif

        return (true, null);
    }

    public async Task<(string? InternalBackupPath, string? SurvivableBackupPath, int PendingOutboxCount)> PrepareDatabaseForUpdateAsync(
        CancellationToken ct = default)
    {
        if (_transactionSync != null)
        {
            try
            {
                var pending = await _transactionSync.GetPendingOutboxItemsAsync();
                foreach (var item in pending)
                {
                    await _transactionSync.UploadTransactionAsync(item, _httpClient);
                }
            }
            catch
            {
                // Offline or unreachable server: preserve pending outbox records in local SQLite
            }
        }

        await LocalPosDbContext.WriteLock.WaitAsync(ct);
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            int pendingCount = await db.SyncOutboxes.CountAsync(o => o.Status != "SYNCED", ct);

            var internalBackup = LocalDatabaseBackupService.CreateBackupSnapshot(db.DatabasePath, "pre_apk_update");
            var survivableBackup = LocalDatabaseBackupService.CreateEncryptedSurvivableBackup(db.DatabasePath);

            return (internalBackup, survivableBackup, pendingCount);
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }
    }

    public async Task<AppReleaseManifestDto?> CheckForAndroidUpdateAsync(int currentVersionCode, CancellationToken ct = default)
    {
        var manifest = await _httpClient.GetFromJsonAsync<AppReleaseManifestDto>("api/app-releases/android/latest", ct);
        if (manifest == null) return null;

        var (isValid, _) = ValidateReleaseManifest(manifest, currentVersionCode);
        return isValid ? manifest : null;
    }

#if ANDROID
    private static (bool IsValid, string? ErrorMessage) VerifyAndroidArchiveIdentity(string apkFilePath, AppReleaseManifestDto manifest)
    {
        try
        {
            var context = Android.App.Application.Context;
            var pm = context.PackageManager;
            if (pm == null) return (false, "Android PackageManager is unavailable.");

#pragma warning disable CA1422
            var flags = Android.Content.PM.PackageInfoFlags.SigningCertificates;
            var archiveInfo = pm.GetPackageArchiveInfo(apkFilePath, flags);
#pragma warning restore CA1422
            if (archiveInfo == null)
                return (false, "Downloaded file is not a valid Android APK archive.");

            if (!string.Equals(archiveInfo.PackageName, ExpectedPackageId, StringComparison.Ordinal))
                return (false, $"APK package name '{archiveInfo.PackageName}' does not match '{ExpectedPackageId}'.");

            if (archiveInfo.LongVersionCode != manifest.VersionCode)
                return (false, $"APK versionCode ({archiveInfo.LongVersionCode}) does not match signed manifest ({manifest.VersionCode}).");

            var signers = archiveInfo.SigningInfo?.GetApkContentsSigners();
            var certBytes = signers?.FirstOrDefault()?.ToByteArray();
            if (certBytes == null || certBytes.Length == 0)
                return (false, "Downloaded APK does not contain a valid v2/v3 signing certificate.");

            var actualCertSha = Convert.ToHexString(SHA256.HashData(certBytes));
            var expectedCertSha = (manifest.SigningCertSha256 ?? string.Empty).Replace(":", string.Empty).Trim().ToUpperInvariant();
            if (!string.IsNullOrEmpty(expectedCertSha) &&
                !string.Equals(actualCertSha, expectedCertSha, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Downloaded APK signing certificate does not match trusted publisher certificate.");
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Android APK archive verification failed: {ex.Message}");
        }
    }

    public static (bool Launched, string Message) LaunchAndroidApkInstaller(string apkFilePath)
    {
        try
        {
            var context = Android.App.Application.Context;
            var pm = context.PackageManager;
            if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O && pm != null && !pm.CanRequestPackageInstalls())
            {
                var permIntent = new Android.Content.Intent(
                    Android.Provider.Settings.ActionManageUnknownAppSources,
                    Android.Net.Uri.Parse($"package:{context.PackageName}"));
                permIntent.AddFlags(Android.Content.ActivityFlags.NewTask);
                context.StartActivity(permIntent);
                return (false, "Please grant 'Install unknown apps' permission for AGRES POS, then tap Update again.");
            }

            var apkFile = new Java.IO.File(apkFilePath);
            var apkUri = Microsoft.Maui.Storage.FileProvider.GetUriForFile(
                context,
                $"{context.PackageName}.fileProvider",
                apkFile);

            var installIntent = new Android.Content.Intent(Android.Content.Intent.ActionView);
            installIntent.SetDataAndType(apkUri, "application/vnd.android.package-archive");
            installIntent.AddFlags(Android.Content.ActivityFlags.GrantReadUriPermission | Android.Content.ActivityFlags.NewTask);
            context.StartActivity(installIntent);
            return (true, "Android package installer launched.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed to launch Android APK installer: {ex.Message}");
        }
    }
#endif
}
