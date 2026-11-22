using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using POS.Api.Controllers;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Tests;

public static partial class Program
{
    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "POS.Api";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task TestProd01_ProductionConfigValidationHealthSanitizationAndReleaseManifest()
    {
        static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
            new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        // 1. Development skips production validation
        ProductionConfigValidator.ValidateProductionConfiguration(BuildConfig(new()), isDevelopment: true);

        // 2. Production rejects missing DB connection, UAT password, UAT JWT key, short JWT key, and ZReading bypass
        var badProd = BuildConfig(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=prod;Username=pos;Password=Agres4u@2026;",
            ["Jwt:Key"] = "BjQYAiIS5mfYIw296Obh8sTtvMtdfPl2",
            ["ZReading:BypassPeriodEndForTesting"] = "true"
        });
        bool threwBad = false;
        try
        {
            ProductionConfigValidator.ValidateProductionConfiguration(badProd, isDevelopment: false);
        }
        catch (InvalidOperationException ex)
        {
            threwBad = ex.Message.Contains("UAT database password")
                && ex.Message.Contains("UAT/development signing key")
                && ex.Message.Contains("BypassPeriodEndForTesting");
        }
        Assert(threwBad, "ProductionConfigValidator must reject UAT credentials, UAT JWT key, and ZReading test bypass in Production.");

        // 3. Production rejects short JWT key (< 32 bytes)
        var shortKeyProd = BuildConfig(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=prod;Username=pos_app;Password=StrongProdPass!2026;",
            ["Jwt:Key"] = "too-short-secret"
        });
        bool threwShort = false;
        try
        {
            ProductionConfigValidator.ValidateProductionConfiguration(shortKeyProd, isDevelopment: false);
        }
        catch (InvalidOperationException ex)
        {
            threwShort = ex.Message.Contains("at least 32 bytes");
        }
        Assert(threwShort, "ProductionConfigValidator must reject JWT signing keys shorter than 32 bytes.");

        // 4. Valid production config passes + CORS origins normalized
        var goodProd = BuildConfig(new()
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Database=prod;Username=pos_app;Password=StrongProdPass!2026;",
            ["Jwt:Key"] = "A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8S9t0U1v2W3x4",
            ["ZReading:BypassPeriodEndForTesting"] = "false",
            ["Cors:AllowedOrigins:0"] = "https://agres.home.arpa/",
            ["Cors:AllowedOrigins:1"] = "https://agres.home.arpa"
        });
        ProductionConfigValidator.ValidateProductionConfiguration(goodProd, isDevelopment: false);
        var origins = ProductionConfigValidator.ResolveCorsAllowedOrigins(goodProd);
        Assert(origins.Length == 1 && origins[0] == "https://agres.home.arpa", "CORS allowed origins must be trimmed and deduplicated.");

        // 5. AppReleasesController returns 404 when configured with unpopulated Production manifest (never serves UAT URL or stale v1.0.2)
        var memOpts = new DbContextOptionsBuilder<PosDbContext>().UseSqlite("Data Source=:memory:").Options;
        using var apiDb = new PosDbContext(memOpts);
        var unconfiguredReleases = new AppReleasesController(apiDb, BuildConfig(new()
        {
            ["AppReleases:Android:VersionCode"] = "0",
            ["AppReleases:Android:Sha256"] = "",
            ["AppReleases:Android:DownloadUrl"] = "https://agres.home.arpa/releases/android/latest.apk"
        }));
        var notFoundResult = unconfiguredReleases.GetLatestAndroidRelease();
        Assert(notFoundResult.Result is NotFoundObjectResult, "Unconfigured Production AppReleases manifest must return 404 NotFound.");

        var validSha = new string('A', 64);
        var configuredReleases = new AppReleasesController(apiDb, BuildConfig(new()
        {
            ["AppReleases:Android:VersionCode"] = "7",
            ["AppReleases:Android:VersionName"] = "1.0.6",
            ["AppReleases:Android:Sha256"] = validSha,
            ["AppReleases:Android:DownloadUrl"] = "https://agres.home.arpa/releases/android/com.agres.pos.client-v1.0.6-universal-signed.apk",
            ["AppReleases:Android:MinRequiredSchemaVersion"] = "8"
        }));
        var okRelease = configuredReleases.GetLatestAndroidRelease().Result as OkObjectResult;
        var dto = okRelease?.Value as AppReleaseManifestDto;
        Assert(dto != null && dto.VersionCode == 7 && dto.DownloadUrl.StartsWith("https://agres.home.arpa/"),
            "Configured Production AppReleases manifest must serve signed https://agres.home.arpa/ metadata.");

        // 6. OfflineSyncController.GetSyncHealth does not leak raw DB exception strings in Production
        var brokenOpts = new DbContextOptionsBuilder<PosDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=nonexistent;Timeout=1;").Options;
        using var brokenDb = new PosDbContext(brokenOpts);
        var prodHealthCtrl = new OfflineSyncController(brokenDb, new StubHostEnvironment(Environments.Production));
        var healthRes = await prodHealthCtrl.GetSyncHealth();
        var objRes = healthRes as ObjectResult;
        Assert(objRes != null && objRes.StatusCode == 503, "Broken DB health check must return 503.");
        string serialized = System.Text.Json.JsonSerializer.Serialize(objRes!.Value);
        Assert(!serialized.Contains("127.0.0.1") && !serialized.Contains("Connection refused") && !serialized.Contains("SocketException"),
            $"Production health response must not expose raw DB exception details: {serialized}");
    }
}
