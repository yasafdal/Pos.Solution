using System.Globalization;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Shared.Services;

/// <summary>
/// Aggregated weight total for a single unit of measure on a receipt.
/// </summary>
public sealed record WeightTotalEntry(string Unit, decimal TotalWeight, string FormattedWeight)
{
    public string FormattedWithUnit => $"{FormattedWeight} {Unit}";
}

/// <summary>
/// Receipt item-count and per-unit weight summary computed from exact decimal quantities.
/// </summary>
public sealed record ReceiptSummaryInfo(
    decimal TotalItemsCount,
    string TotalItemsDisplay,
    IReadOnlyList<WeightTotalEntry> WeightTotals)
{
    public bool HasWeightTotals => WeightTotals.Count > 0;
}

/// <summary>
/// Shared receipt presentation helpers for unit resolution, item subline formatting, and item/weight summaries.
/// </summary>
public static class ReceiptPresentationHelper
{
    /// <summary>
    /// Returns true if the item uses weight-based pricing or has an unspecified pricing method
    /// with a non-complimentary fractional quantity. Explicit non-weight pricing methods return false.
    /// </summary>
    public static bool IsWeightBasedItem(OrderItemResponseDto? item)
    {
        if (item is null)
            return false;

        if (PricingMethods.IsWeightBased(item.PricingMethod))
            return true;

        return string.IsNullOrWhiteSpace(item.PricingMethod)
            && !item.IsComplimentary
            && item.Quantity != decimal.Truncate(item.Quantity);
    }

    /// <summary>
    /// Resolves the trimmed unit label for receipt display, defaulting weight-based items to "kg"
    /// and returning null for non-weight items with no explicit unit of measure.
    /// </summary>
    public static string? ResolveDisplayUnit(OrderItemResponseDto? item)
    {
        if (item is null)
            return null;

        var trimmedUnit = string.IsNullOrWhiteSpace(item.UnitOfMeasure)
            ? null
            : item.UnitOfMeasure.Trim();

        if (IsWeightBasedItem(item))
            return trimmedUnit ?? "kg";

        return trimmedUnit;
    }

    /// <summary>
    /// Formats a monetary value with two invariant decimal places and the given currency prefix.
    /// </summary>
    public static string FormatMoney(decimal value, string currencyPrefix = "PHP ") =>
        $"{currencyPrefix ?? string.Empty}{value.ToString("N2", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Formats the receipt quantity, unit, and unit-price subline for an order item.
    /// </summary>
    public static string FormatItemSubline(OrderItemResponseDto? item, string currencyPrefix = "PHP ")
    {
        if (item is null)
            return string.Empty;

        var qtyText = CartItem.FormatQuantity(item.Quantity);
        var priceText = FormatMoney(item.UnitPrice, currencyPrefix);
        var unit = ResolveDisplayUnit(item);

        if (IsWeightBasedItem(item))
            return $"{qtyText} {unit} x {priceText}/{unit}";

        return !string.IsNullOrWhiteSpace(unit)
            ? $"{qtyText} {unit} x {priceText}"
            : $"{qtyText} x {priceText}";
    }

    /// <summary>
    /// Returns true when a zero-price modifier should display a "(Free)" label,
    /// covering linked modifiers and unlinked complimentary modifiers on weight-based items
    /// while leaving regular zero-price instruction notes unlabeled.
    /// </summary>
    public static bool ShouldShowFreeModifierLabel(
        OrderItemResponseDto? item,
        OrderItemModifierResponseDto? modifier)
    {
        if (item is null || modifier is null || modifier.AdditionalPrice != 0m)
            return false;

        return modifier.ProductId.HasValue || IsWeightBasedItem(item);
    }

    /// <summary>
    /// Summarizes total item count and per-unit weight totals for a receipt.
    /// </summary>
    public static ReceiptSummaryInfo Summarize(ReceiptDto? receipt) =>
        SummarizeItems(receipt?.Items);

    /// <summary>
    /// Summarizes total item count and per-unit weight totals across a sequence of order items.
    /// </summary>
    public static ReceiptSummaryInfo SummarizeItems(IEnumerable<OrderItemResponseDto>? items)
    {
        decimal totalItems = 0m;
        var weightSums = new List<(string Unit, decimal Sum)>();
        var unitIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (items is not null)
        {
            foreach (var item in items)
            {
                if (item is null)
                    continue;

                if (IsWeightBasedItem(item))
                {
                    totalItems += 1m;
                    var unit = ResolveDisplayUnit(item) ?? "kg";
                    if (unitIndices.TryGetValue(unit, out int existingIndex))
                    {
                        var existing = weightSums[existingIndex];
                        weightSums[existingIndex] = (existing.Unit, existing.Sum + item.Quantity);
                    }
                    else
                    {
                        unitIndices[unit] = weightSums.Count;
                        weightSums.Add((unit, item.Quantity));
                    }
                }
                else
                {
                    totalItems += item.Quantity;
                }
            }
        }

        IReadOnlyList<WeightTotalEntry> weightTotals = weightSums.Count == 0
            ? Array.Empty<WeightTotalEntry>()
            : Array.AsReadOnly(weightSums
                .Select(entry => new WeightTotalEntry(
                    entry.Unit,
                    entry.Sum,
                    CartItem.FormatQuantity(entry.Sum)))
                .ToArray());

        return new ReceiptSummaryInfo(
            totalItems,
            CartItem.FormatQuantity(totalItems),
            weightTotals);
    }
}
