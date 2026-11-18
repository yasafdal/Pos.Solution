using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using POS.Shared.Services;

namespace POS.Shared.Models;

public class AppReleaseManifestDto
{
    public string PackageId { get; set; } = "com.agres.pos.client";
    public int VersionCode { get; set; }
    public string VersionName { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public int MinRequiredSchemaVersion { get; set; } = 2;
    public string SigningCertSha256 { get; set; } = string.Empty;
    public string EnvironmentType { get; set; } = LicenseEnvironmentConstants.Production;
    public DateTime PublishedAtUtc { get; set; }
    public string? ReleaseNotes { get; set; }
    public string Signature { get; set; } = string.Empty;

    public static string BuildCanonicalString(AppReleaseManifestDto manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var publishedStr = LicenseCryptoService.NormalizeUtcSecond(manifest.PublishedAtUtc)
            .ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var env = LicenseCryptoService.NormalizeEnvironment(manifest.EnvironmentType);
        var pkg = (manifest.PackageId ?? string.Empty).Trim();
        var verName = (manifest.VersionName ?? string.Empty).Trim();
        var sha = (manifest.Sha256 ?? string.Empty).Trim().ToUpperInvariant();
        var url = (manifest.DownloadUrl ?? string.Empty).Trim();
        var certSha = (manifest.SigningCertSha256 ?? string.Empty).Replace(":", string.Empty).Trim().ToUpperInvariant();

        return string.Create(CultureInfo.InvariantCulture,
            $"POS-APK-REL-V1|{pkg}|{manifest.VersionCode}|{verName}|{sha}|{url}|{manifest.MinRequiredSchemaVersion}|{certSha}|{env}|{publishedStr}");
    }

    public static string SignManifest(AppReleaseManifestDto manifest, ECParameters privateKeyParameters)
    {
        var canonical = BuildCanonicalString(manifest);
        var data = Encoding.UTF8.GetBytes(canonical);
        using var ecdsa = ECDsa.Create(privateKeyParameters);
        var sig = ecdsa.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return Convert.ToBase64String(sig);
    }

    public static bool VerifyManifestSignature(AppReleaseManifestDto? manifest, string? requiredEnvironment = null)
    {
        if (manifest == null || string.IsNullOrWhiteSpace(manifest.Signature))
            return false;

        var env = LicenseCryptoService.NormalizeEnvironment(manifest.EnvironmentType);
        if (!string.IsNullOrWhiteSpace(requiredEnvironment))
        {
            var expectedEnv = LicenseCryptoService.NormalizeEnvironment(requiredEnvironment);
            if (!string.Equals(env, expectedEnv, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        try
        {
            var sigBytes = Convert.FromBase64String(manifest.Signature.Trim());
            var canonical = BuildCanonicalString(manifest);
            var data = Encoding.UTF8.GetBytes(canonical);
            var pubParams = LicenseCryptoService.GetPublicKeyParameters(env);
            using var ecdsa = ECDsa.Create(pubParams);
            return ecdsa.VerifyData(data, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch
        {
            return false;
        }
    }
}
