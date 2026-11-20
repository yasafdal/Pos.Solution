using System;
using System.Globalization;
using POS.Shared.Services;

namespace POS.Shared.Constants;

/// <summary>
/// Authoritative Business Date expiration and cancellation rules for Held Sales across POS.Api, POS.Client, and POS.Web.
/// Uses BusinessDayClock (4:00 AM cutoff) consistently across all layers.
/// </summary>
public static class HeldSalePolicies
{
    public const string PreviousBusinessDayCancellationReason =
        "Automatically cancelled because the Held Sale belongs to a previous Business Date.";

    public const string ManualCancellationReason =
        "Transaction cancelled and removed.";

    public const string ExpiredHeldSaleResumeMessage =
        "This Held Sale has expired because it belongs to a previous business day.";

    public static string FormatBusinessDate(DateOnly date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static DateOnly ResolveBusinessDate(string? storedBusinessDate, DateTime createdAt)
    {
        if (!string.IsNullOrWhiteSpace(storedBusinessDate) &&
            DateOnly.TryParseExact(storedBusinessDate.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        // HeldSale.CreatedAt and LocalHeldSale.CreatedAt are persisted in UTC; normalize Unspecified values as UTC
        // before applying the authoritative 4:00 AM Asia/Manila business-day cutoff.
        return BusinessDayClock.GetBusinessDate(BusinessDayClock.NormalizeUtc(createdAt));
    }

    public static bool IsPreviousBusinessDate(string? storedBusinessDate, DateTime createdAt, DateOnly? currentBusinessDate = null)
    {
        var current = currentBusinessDate ?? BusinessDayClock.GetCurrentBusinessDate();
        return ResolveBusinessDate(storedBusinessDate, createdAt) < current;
    }

    public static bool IsCurrentBusinessDate(string? storedBusinessDate, DateTime createdAt, DateOnly? currentBusinessDate = null)
    {
        var current = currentBusinessDate ?? BusinessDayClock.GetCurrentBusinessDate();
        return ResolveBusinessDate(storedBusinessDate, createdAt) == current;
    }
}
