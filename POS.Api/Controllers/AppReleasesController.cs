using System;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Api.Controllers;

[Route("api/app-releases")]
[ApiController]
[Authorize]
public class AppReleasesController : ControllerBase
{
    private readonly PosDbContext _context;
    private readonly IConfiguration? _configuration;

    public AppReleasesController(PosDbContext context, IConfiguration? configuration = null)
    {
        _context = context;
        _configuration = configuration;
    }

    [HttpGet("android/latest")]
    public ActionResult<AppReleaseManifestDto> GetLatestAndroidRelease()
    {
        var licensingService = new LicensingService(_context, _configuration);
        string env = licensingService.GetConfiguredServerEnvironment();

        int versionCode = int.TryParse(_configuration?["AppReleases:Android:VersionCode"], out int cfgVerCode) && cfgVerCode > 0
            ? cfgVerCode
            : 3;
        string versionName = _configuration?["AppReleases:Android:VersionName"] ?? "1.0.2";
        string sha256 = _configuration?["AppReleases:Android:Sha256"]
            ?? "3A1956E43F2789CAFA3F3DF5D22DAB8FDEBF656E324788FF492570F4044B5097";
        string downloadUrl = _configuration?["AppReleases:Android:DownloadUrl"]
            ?? "https://dev-uat.agressolutions.com/releases/android/com.agres.pos.client-latest-universal-signed.apk";
        int minSchema = int.TryParse(_configuration?["AppReleases:Android:MinRequiredSchemaVersion"], out int cfgSchema) && cfgSchema > 0
            ? cfgSchema
            : 2;
        string certSha256 = _configuration?["AppReleases:Android:SigningCertSha256"]
            ?? "9A74092B9AF0A0B91C1A962C63B33DE4012C6068FF1E19B9648AC0AAF2C7E757";
        DateTime publishedAt = DateTime.TryParse(_configuration?["AppReleases:Android:PublishedAtUtc"], out var parsedPub)
            ? LicenseCryptoService.NormalizeUtcSecond(parsedPub)
            : new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);

        var manifest = new AppReleaseManifestDto
        {
            PackageId = "com.agres.pos.client",
            VersionCode = versionCode,
            VersionName = versionName,
            Sha256 = sha256.Trim().ToUpperInvariant(),
            DownloadUrl = downloadUrl.Trim(),
            MinRequiredSchemaVersion = minSchema,
            SigningCertSha256 = certSha256.Replace(":", string.Empty).Trim().ToUpperInvariant(),
            EnvironmentType = env,
            PublishedAtUtc = publishedAt,
            ReleaseNotes = _configuration?["AppReleases:Android:ReleaseNotes"]
                ?? "Offline SQLite preservation, versioned schema migrations, and survivable tablet backup."
        };

        var privKey = licensingService.GetServerPrivateKeyParameters(env);
        manifest.Signature = AppReleaseManifestDto.SignManifest(manifest, privKey);
        return Ok(manifest);
    }
}
