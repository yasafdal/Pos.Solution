using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace POS.Api.Services;

public static class ProductionConfigValidator
{
    internal const string KnownUatJwtKey = "BjQYAiIS5mfYIw296Obh8sTtvMtdfPl2";
    internal const string KnownUatDbPassword = "Agres4u@2026";
    private const int MinimumJwtKeyBytes = 32;

    public static void ValidateProductionConfiguration(IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (isDevelopment)
            return;

        var errors = new List<string>();

        string? connStr = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            errors.Add("ConnectionStrings:DefaultConnection is required in non-Development environments.");
        }
        else if (connStr.Contains(KnownUatDbPassword, StringComparison.Ordinal))
        {
            errors.Add("ConnectionStrings:DefaultConnection must not reuse the committed UAT database password in Production.");
        }

        string? jwtKey = configuration["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(jwtKey))
        {
            errors.Add("Jwt:Key is required in non-Development environments (set via Jwt__Key environment variable).");
        }
        else
        {
            if (string.Equals(jwtKey.Trim(), KnownUatJwtKey, StringComparison.Ordinal))
                errors.Add("Jwt:Key must not reuse the committed UAT/development signing key in Production.");

            if (Encoding.UTF8.GetByteCount(jwtKey) < MinimumJwtKeyBytes)
                errors.Add($"Jwt:Key must be at least {MinimumJwtKeyBytes} bytes for HMAC-SHA256.");
        }

        if (configuration.GetValue<bool>("ZReading:BypassPeriodEndForTesting"))
        {
            errors.Add("ZReading:BypassPeriodEndForTesting must be false in Production.");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Production configuration validation failed:\n - " + string.Join("\n - ", errors));
        }
    }

    public static string[] ResolveCorsAllowedOrigins(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetSection("Cors:AllowedOrigins")
            .Get<string[]>()?
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();
    }
}
