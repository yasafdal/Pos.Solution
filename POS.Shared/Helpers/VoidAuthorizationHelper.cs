using System;
using System.Security.Cryptography;
using System.Text;

namespace POS.Shared.Helpers;

/// <summary>
/// Cryptographic helpers shared between POS.Client and POS.Api for offline void authorization proofs.
/// Never stores or transmits plaintext PINs in outbox payloads or local void records.
/// </summary>
public static class VoidAuthorizationHelper
{
    public const string NoAuthorizedSupervisorMessage =
        "Offline void is unavailable because no authorized supervisor credentials are available on this terminal.";

    public static string ComputePinHash(string rawPin)
    {
        if (string.IsNullOrWhiteSpace(rawPin))
            return string.Empty;

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPin.Trim())));
    }

    public static string ComputeAuthorizationProofHash(
        string clientVoidId,
        string? invoiceOrOrderRef,
        int branchId,
        int terminalId,
        string? deviceIdentifier,
        string approvedBy,
        string supervisorPinHash,
        DateTime voidedAtUtc)
    {
        var normalizedUtc = voidedAtUtc.Kind == DateTimeKind.Local
            ? voidedAtUtc.ToUniversalTime()
            : DateTime.SpecifyKind(voidedAtUtc, DateTimeKind.Utc);

        string canonical = string.Join('|',
            "AGRES-OFFLINE-VOID-V1",
            (clientVoidId ?? string.Empty).Trim(),
            (invoiceOrOrderRef ?? string.Empty).Trim().ToUpperInvariant(),
            branchId.ToString(),
            terminalId.ToString(),
            (deviceIdentifier ?? string.Empty).Trim().ToLowerInvariant(),
            (approvedBy ?? string.Empty).Trim().ToLowerInvariant(),
            (supervisorPinHash ?? string.Empty).Trim().ToUpperInvariant(),
            normalizedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static bool FixedTimeEqualsHex(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;

        var bytesA = Encoding.UTF8.GetBytes(a.Trim().ToUpperInvariant());
        var bytesB = Encoding.UTF8.GetBytes(b.Trim().ToUpperInvariant());
        return bytesA.Length == bytesB.Length && CryptographicOperations.FixedTimeEquals(bytesA, bytesB);
    }
}
