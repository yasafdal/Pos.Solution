using System;
using System.Collections.Generic;
using System.Linq;
using POS.Shared.Models;

namespace POS.Shared.Services;

/// <summary>
/// Centralized, shared discount validation and calculation engine used identically by POS.Client (offline/preview)
/// and POS.Api (server-side enforcement). Guarantees FinalPayableAmount >= 0.00 and rejects invalid discount inputs.
/// </summary>
public class LocalDiscountCalculationService
{
    public const decimal MaxPercentageLimit = 100.00m;

    public static bool IsStatutoryVatExemptCode(string? code)
    {
        var normalized = (code ?? "NONE").Trim().ToUpperInvariant();
        return normalized is "SC" or "PWD" or "NAAC" or "MOV" or "ATHLETE" or "VALOR";
    }

    public static bool IsStatutoryCode(string? code)
    {
        var normalized = (code ?? "NONE").Trim().ToUpperInvariant();
        return IsStatutoryVatExemptCode(normalized) || normalized is "SP" or "SOLO_PARENT" or "BNPC";
    }

    /// <summary>
    /// Validates admin-configured DiscountType definitions (create/update) across POS.Web and POS.Api.
    /// </summary>
    public static string? ValidateDiscountTypeDefinition(string? code, string? method, decimal value)
    {
        var normalizedMethod = (method ?? "PERCENTAGE").Trim().ToUpperInvariant();
        if (normalizedMethod is not ("PERCENTAGE" or "FIXED"))
            return "Invalid discount method. Supported methods are PERCENTAGE and FIXED.";

        if (value < 0m)
            return "Invalid discount value. Discount value cannot be negative.";

        if (normalizedMethod == "PERCENTAGE" && value > MaxPercentageLimit)
            return "Discount percentage cannot exceed the allowed limit (0% to 100%).";

        return null;
    }

    /// <summary>
    /// Validates financial invariants for synchronized offline transactions before PostgreSQL persistence.
    /// </summary>
    public static string? ValidateSyncedTransactionTotals(
        decimal subtotal,
        decimal discountAmount,
        decimal totalAmount,
        decimal vatableSales,
        decimal vatExemptSales,
        decimal zeroRatedSales,
        decimal vatAmount,
        IEnumerable<OfflineTransactionItemDto>? items,
        IEnumerable<OfflinePaymentDto>? payments)
    {
        if (subtotal < 0m)
            return "Invalid transaction subtotal: subtotal cannot be negative.";

        if (discountAmount < 0m)
            return "Invalid discount value: discount amount cannot be negative.";

        if (discountAmount > subtotal + 0.01m)
            return "The applied discounts exceed the eligible transaction amount.";

        if (totalAmount < 0m)
            return "Invalid transaction total: final payable amount cannot be negative.";

        if (totalAmount > subtotal + 0.01m)
            return "Invalid transaction total: payable amount cannot exceed subtotal.";

        if (vatableSales < 0m || vatExemptSales < 0m || zeroRatedSales < 0m || vatAmount < 0m)
            return "Invalid tax breakdown: VAT or sales components cannot be negative.";

        if (items != null)
        {
            foreach (var item in items)
            {
                if (item.Quantity <= 0m)
                    return "Invalid item quantity: quantity must be greater than zero.";
                if (item.UnitPrice < 0m || item.LineTotal < 0m)
                    return "Invalid item pricing: unit price and line total cannot be negative.";
            }
        }

        if (payments != null)
        {
            foreach (var p in payments)
            {
                if (p.Amount < 0m || p.TenderedAmount < 0m || p.ChangeAmount < 0m)
                    return "Invalid payment amount: payment, tendered, and change amounts cannot be negative.";
            }
        }

        return null;
    }

    public OrderCalculationResultDto CalculateOrder(OrderCalculationRequestDto request)
    {
        if (request == null)
            throw new ArgumentException("Calculation request cannot be null.");

        var items = request.Items ?? new List<CartItemCalculationDto>();
        string dCode = (request.DiscountCode ?? "NONE").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(dCode)) dCode = "NONE";
        string? dMethod = string.IsNullOrWhiteSpace(request.DiscountMethod)
            ? null
            : request.DiscountMethod.Trim().ToUpperInvariant();

        // 1. Validate request-level discount inputs BEFORE performing calculations
        if (request.ManualDiscountPercentage < 0m)
            throw new ArgumentException("Invalid discount value. Discount percentage cannot be negative.");
        if (request.ManualDiscountPercentage > MaxPercentageLimit)
            throw new ArgumentException("Discount percentage cannot exceed the allowed limit (0% to 100%).");

        if (request.ManualDiscountAmount < 0m)
            throw new ArgumentException("Invalid discount value. Discount amount cannot be negative.");

        if (request.PromoDiscountAmount < 0m)
            throw new ArgumentException("Invalid discount value. Promotional discount amount cannot be negative.");

        if (dMethod != null && dMethod is not ("PERCENTAGE" or "FIXED"))
            throw new ArgumentException("Invalid discount method. Must be PERCENTAGE or FIXED.");

        if (request.DiscountValue.HasValue)
        {
            if (request.DiscountValue.Value < 0m)
                throw new ArgumentException("Invalid discount value. Discount value cannot be negative.");

            if ((dMethod == null || dMethod == "PERCENTAGE") && request.DiscountValue.Value > MaxPercentageLimit)
                throw new ArgumentException("Discount percentage cannot exceed the allowed limit (0% to 100%).");
        }

        if (dCode == "MANUAL" && request.ManualDiscountPercentage > 0m && request.ManualDiscountAmount > 0m)
            throw new ArgumentException("Specify either a manual discount percentage or a manual fixed amount, not both.");

        bool isStatutory = IsStatutoryCode(dCode);
        bool hasExtraOrderStack =
            request.ManualDiscountPercentage > 0m ||
            request.ManualDiscountAmount > 0m ||
            request.PromoDiscountAmount > 0m;

        if (isStatutory && hasExtraOrderStack)
            throw new ArgumentException("Statutory discounts (SC/PWD/NAAC/MOV/SP/BNPC) cannot be combined with additional manual or promotional discounts.");

        // 2. Validate items & compute per-line gross and optional item-level discounts
        bool isVatReg = (request.BranchTaxType ?? "VAT-Registered").Equals("VAT-Registered", StringComparison.OrdinalIgnoreCase);

        var lineStates = new List<LineWorkingState>(items.Count);
        foreach (var item in items)
        {
            if (item.Quantity <= 0m)
                throw new ArgumentException("Item quantity must be greater than zero.");
            if (item.UnitPrice < 0m || item.ModifierTotal < 0m || (item.TargetAmount.HasValue && item.TargetAmount.Value < 0m))
                throw new ArgumentException("Item prices cannot be negative.");
            if (item.ItemDiscountPercentage < 0m)
                throw new ArgumentException("Invalid discount value. Item discount percentage cannot be negative.");
            if (item.ItemDiscountPercentage > MaxPercentageLimit)
                throw new ArgumentException("Discount percentage cannot exceed the allowed limit (0% to 100%).");
            if (item.ItemDiscountAmount < 0m)
                throw new ArgumentException("Invalid discount value. Item discount amount cannot be negative.");

            if (isStatutory && (item.ItemDiscountPercentage > 0m || item.ItemDiscountAmount > 0m))
                throw new ArgumentException("Statutory discounts (SC/PWD/NAAC/MOV/SP/BNPC) cannot be combined with item-level discounts.");

            decimal itemEffectivePrice = item.IsComplimentary ? 0m : (item.UnitPrice + item.ModifierTotal);
            decimal lineGross = item.IsComplimentary
                ? 0m
                : (item.TargetAmount.HasValue && item.TargetAmount.Value > 0m
                    ? Math.Round(item.TargetAmount.Value, 2, MidpointRounding.AwayFromZero)
                    : Math.Round(itemEffectivePrice * item.Quantity, 2, MidpointRounding.AwayFromZero));

            decimal itemLevelDiscount = 0m;
            if (!item.IsComplimentary && lineGross > 0m)
            {
                decimal pctPart = item.ItemDiscountPercentage > 0m
                    ? Math.Round(lineGross * (item.ItemDiscountPercentage / 100m), 2, MidpointRounding.AwayFromZero)
                    : 0m;
                decimal remainingAfterPct = Math.Max(0m, lineGross - pctPart);
                if (item.ItemDiscountAmount > remainingAfterPct)
                    throw new ArgumentException("Discount amount cannot exceed the eligible amount.");

                itemLevelDiscount = Math.Min(lineGross, pctPart + Math.Round(item.ItemDiscountAmount, 2, MidpointRounding.AwayFromZero));
            }
            else if (item.ItemDiscountAmount > 0m)
            {
                throw new ArgumentException("Discount amount cannot exceed the eligible amount.");
            }

            string taxName = (item.TaxName ?? "VATable").ToUpperInvariant();
            bool isVatExempt = taxName.Contains("EXEMPT");
            bool isZeroRated = taxName.Contains("ZERO");

            lineStates.Add(new LineWorkingState
            {
                Item = item,
                EffectivePrice = itemEffectivePrice,
                LineGross = lineGross,
                ItemLevelDiscount = itemLevelDiscount,
                RemainingEligible = Math.Max(0m, lineGross - itemLevelDiscount),
                IsVatExempt = isVatExempt,
                IsZeroRated = isZeroRated
            });
        }

        // 3. Apply primary order-level percentage or statutory discount against remaining eligible amount
        decimal orderFixedDiscountToAllocate = 0m;

        if (dCode == "MANUAL")
        {
            if (request.ManualDiscountAmount > 0m)
            {
                orderFixedDiscountToAllocate += Math.Round(request.ManualDiscountAmount, 2, MidpointRounding.AwayFromZero);
            }
            else if (dMethod == "FIXED" && request.DiscountValue.HasValue && request.DiscountValue.Value > 0m)
            {
                orderFixedDiscountToAllocate += Math.Round(request.DiscountValue.Value, 2, MidpointRounding.AwayFromZero);
            }
            else if (request.ManualDiscountPercentage > 0m || (dMethod == "PERCENTAGE" && request.DiscountValue.HasValue && request.DiscountValue.Value > 0m))
            {
                decimal pct = request.ManualDiscountPercentage > 0m
                    ? request.ManualDiscountPercentage
                    : request.DiscountValue!.Value;
                ApplyOrderPercentageDiscount(lineStates, pct, requireStatutoryEligibility: false);
            }
        }
        else if (IsStatutoryVatExemptCode(dCode))
        {
            foreach (var st in lineStates)
            {
                if (!st.Item.IsEligibleForStatutoryDiscount || st.LineGross <= 0m)
                    continue;

                if (!st.IsVatExempt && !st.IsZeroRated && isVatReg)
                {
                    // BIR Scenario B: VAT-Registered eligible SC/PWD/NAAC/MOV sale
                    // 1. Remove 12% VAT -> VAT-exclusive base
                    decimal vatExclusiveBase = Math.Round(st.LineGross / 1.12m, 2);
                    st.StatutoryVatRemoved = true;
                    st.VatExclusiveBase = vatExclusiveBase;
                    // 2. Apply 20% statutory discount on VAT-exclusive base
                    st.OrderLevelDiscount = Math.Min(vatExclusiveBase, Math.Round(vatExclusiveBase * 0.20m, 2));
                }
                else if (!isVatReg || st.IsVatExempt)
                {
                    // BIR Scenario C: Non-VAT or already VAT-exempt eligible sale
                    st.OrderLevelDiscount = Math.Min(st.RemainingEligible, Math.Round(st.RemainingEligible * 0.20m, 2));
                }
            }
        }
        else if (dCode is "SP" or "SOLO_PARENT")
        {
            decimal spRate = (dMethod == "PERCENTAGE" && request.DiscountValue is > 0m and <= MaxPercentageLimit)
                ? request.DiscountValue.Value
                : 10.00m;
            ApplyOrderPercentageDiscount(lineStates, spRate, requireStatutoryEligibility: true);
        }
        else if (dCode == "BNPC")
        {
            decimal bnpcRate = (dMethod == "PERCENTAGE" && request.DiscountValue is > 0m and <= MaxPercentageLimit)
                ? request.DiscountValue.Value
                : 5.00m;
            ApplyOrderPercentageDiscount(lineStates, bnpcRate, requireStatutoryEligibility: true);
        }
        else if (dCode == "EMPLOYEE")
        {
            if (dMethod == "FIXED" && request.DiscountValue.HasValue && request.DiscountValue.Value > 0m)
            {
                orderFixedDiscountToAllocate += Math.Round(request.DiscountValue.Value, 2, MidpointRounding.AwayFromZero);
            }
            else
            {
                decimal empRate = (dMethod == "PERCENTAGE" && request.DiscountValue is > 0m and <= MaxPercentageLimit)
                    ? request.DiscountValue.Value
                    : 10.00m;
                ApplyOrderPercentageDiscount(lineStates, empRate, requireStatutoryEligibility: false);
            }
        }
        else if (dCode != "NONE")
        {
            // Configured store / promotional / coupon discount resolved via DiscountMethod & DiscountValue
            if (dMethod == "FIXED" && request.DiscountValue.HasValue && request.DiscountValue.Value > 0m)
            {
                orderFixedDiscountToAllocate += Math.Round(request.DiscountValue.Value, 2, MidpointRounding.AwayFromZero);
            }
            else if (dMethod == "PERCENTAGE" && request.DiscountValue.HasValue && request.DiscountValue.Value > 0m)
            {
                ApplyOrderPercentageDiscount(lineStates, request.DiscountValue.Value, requireStatutoryEligibility: false);
            }
        }

        // 4. Add any additional PromoDiscountAmount (e.g., coupon/promo stacked with manual/item discounts)
        if (request.PromoDiscountAmount > 0m)
        {
            orderFixedDiscountToAllocate += Math.Round(request.PromoDiscountAmount, 2, MidpointRounding.AwayFromZero);
        }

        // 5. Validate and allocate fixed order-level discounts against remaining eligible amount
        if (orderFixedDiscountToAllocate > 0m)
        {
            decimal totalRemainingEligible = Math.Round(lineStates.Sum(s => s.RemainingAfterOrderPct), 2);
            if (orderFixedDiscountToAllocate > totalRemainingEligible)
            {
                bool hasMultipleLayers = lineStates.Any(s => s.ItemLevelDiscount > 0m || s.OrderLevelDiscount > 0m)
                    || (request.ManualDiscountAmount > 0m && request.PromoDiscountAmount > 0m);
                throw new ArgumentException(hasMultipleLayers
                    ? "The applied discounts exceed the eligible transaction amount."
                    : "Discount amount cannot exceed the eligible amount.");
            }

            AllocateFixedDiscountProportionally(lineStates, orderFixedDiscountToAllocate, totalRemainingEligible);
        }

        // 6. Finalize per-line totals, tax classifications, and summary invariants (NEVER negative)
        var calculatedItems = new List<CalculatedItemDto>(lineStates.Count);
        foreach (var st in lineStates)
        {
            decimal totalLineDiscount;
            decimal lineTotal;
            decimal vatAmount = 0m;
            string itemVatType;

            if (st.StatutoryVatRemoved)
            {
                totalLineDiscount = Math.Min(st.VatExclusiveBase, Math.Max(0m, st.OrderLevelDiscount));
                lineTotal = Math.Max(0m, Math.Round(st.VatExclusiveBase - totalLineDiscount, 2));
                itemVatType = "EXEMPT";
                vatAmount = 0m;
            }
            else
            {
                totalLineDiscount = Math.Min(st.LineGross, Math.Max(0m, Math.Round(st.ItemLevelDiscount + st.OrderLevelDiscount + st.AllocatedFixedDiscount, 2)));
                lineTotal = Math.Max(0m, Math.Round(st.LineGross - totalLineDiscount, 2));

                if (st.IsVatExempt)
                {
                    itemVatType = "EXEMPT";
                    vatAmount = 0m;
                }
                else if (st.IsZeroRated)
                {
                    itemVatType = "ZERO_RATED";
                    vatAmount = 0m;
                }
                else if (isVatReg)
                {
                    // VAT-inclusive net payable basis: guarantees VatableSales >= 0 and VatAmount == 0 at 100% discount
                    decimal netVatable = Math.Round(lineTotal / 1.12m, 2);
                    vatAmount = Math.Max(0m, Math.Round(lineTotal - netVatable, 2));
                    itemVatType = "VATABLE";
                }
                else
                {
                    itemVatType = "NON_VAT";
                    vatAmount = 0m;
                }
            }

            calculatedItems.Add(new CalculatedItemDto
            {
                ProductId = st.Item.ProductId,
                ProductName = st.Item.ProductName,
                Quantity = st.Item.Quantity,
                OriginalUnitPrice = st.EffectivePrice,
                UnitPrice = st.EffectivePrice,
                LineSubtotal = st.LineGross,
                DiscountAmount = totalLineDiscount,
                LineTotal = lineTotal,
                VatType = itemVatType,
                VatAmount = vatAmount
            });
        }

        var result = new OrderCalculationResultDto
        {
            CalculatedItems = calculatedItems,
            Subtotal = Math.Round(Math.Max(0m, calculatedItems.Sum(i => i.LineSubtotal)), 2),
            DiscountAmount = Math.Round(Math.Max(0m, calculatedItems.Sum(i => i.DiscountAmount)), 2)
        };

        // Guard cumulative discount invariant
        if (result.DiscountAmount > result.Subtotal)
        {
            result.DiscountAmount = result.Subtotal;
        }

        // Final monetary safety invariant: FinalPayableAmount = Math.Max(0m, CalculatedPayableAmount)
        result.TotalAmount = Math.Round(Math.Max(0m, calculatedItems.Sum(i => i.LineTotal)), 2);

        result.VatableSales = Math.Round(Math.Max(0m, calculatedItems.Where(i => i.VatType == "VATABLE").Sum(i => i.LineTotal - i.VatAmount)), 2);
        result.VatExemptSales = Math.Round(Math.Max(0m, calculatedItems.Where(i => i.VatType == "EXEMPT" || i.VatType == "NON_VAT").Sum(i => Math.Min(i.LineSubtotal, i.LineTotal + i.DiscountAmount))), 2);
        result.ZeroRatedSales = Math.Round(Math.Max(0m, calculatedItems.Where(i => i.VatType == "ZERO_RATED").Sum(i => i.LineTotal)), 2);
        result.VatAmount = Math.Round(Math.Max(0m, calculatedItems.Sum(i => i.VatAmount)), 2);

        return result;
    }

    private static void ApplyOrderPercentageDiscount(List<LineWorkingState> lineStates, decimal percentage, bool requireStatutoryEligibility)
    {
        if (percentage <= 0m) return;
        decimal clampedPct = Math.Min(MaxPercentageLimit, percentage);

        foreach (var st in lineStates)
        {
            if (requireStatutoryEligibility && !st.Item.IsEligibleForStatutoryDiscount)
                continue;
            if (st.RemainingEligible <= 0m)
                continue;

            decimal computed = Math.Round(st.RemainingEligible * (clampedPct / 100m), 2, MidpointRounding.AwayFromZero);
            st.OrderLevelDiscount = Math.Min(st.RemainingEligible, computed);
        }
    }

    private static void AllocateFixedDiscountProportionally(List<LineWorkingState> lineStates, decimal fixedAmount, decimal totalRemainingEligible)
    {
        if (fixedAmount <= 0m || totalRemainingEligible <= 0m) return;

        var eligibleLines = lineStates.Where(s => s.RemainingAfterOrderPct > 0m).ToList();
        decimal remainingToAllocate = Math.Min(fixedAmount, totalRemainingEligible);

        for (int i = 0; i < eligibleLines.Count && remainingToAllocate > 0m; i++)
        {
            var line = eligibleLines[i];
            decimal share;
            if (i == eligibleLines.Count - 1)
            {
                share = Math.Min(line.RemainingAfterOrderPct, remainingToAllocate);
            }
            else
            {
                share = Math.Round(fixedAmount * (line.RemainingAfterOrderPct / totalRemainingEligible), 2, MidpointRounding.AwayFromZero);
                share = Math.Min(line.RemainingAfterOrderPct, Math.Min(share, remainingToAllocate));
            }

            line.AllocatedFixedDiscount = share;
            remainingToAllocate = Math.Round(remainingToAllocate - share, 2);
        }

        // Sweep any 0.01 rounding remainder onto lines with remaining headroom
        if (remainingToAllocate > 0m)
        {
            foreach (var line in eligibleLines)
            {
                if (remainingToAllocate <= 0m) break;
                decimal headroom = Math.Round(line.RemainingAfterOrderPct - line.AllocatedFixedDiscount, 2);
                if (headroom <= 0m) continue;
                decimal add = Math.Min(headroom, remainingToAllocate);
                line.AllocatedFixedDiscount = Math.Round(line.AllocatedFixedDiscount + add, 2);
                remainingToAllocate = Math.Round(remainingToAllocate - add, 2);
            }
        }
    }

    private sealed class LineWorkingState
    {
        public CartItemCalculationDto Item { get; init; } = null!;
        public decimal EffectivePrice { get; init; }
        public decimal LineGross { get; init; }
        public decimal ItemLevelDiscount { get; init; }
        public decimal RemainingEligible { get; init; }
        public bool IsVatExempt { get; init; }
        public bool IsZeroRated { get; init; }
        public bool StatutoryVatRemoved { get; set; }
        public decimal VatExclusiveBase { get; set; }
        public decimal OrderLevelDiscount { get; set; }
        public decimal AllocatedFixedDiscount { get; set; }
        public decimal RemainingAfterOrderPct => Math.Max(0m, RemainingEligible - OrderLevelDiscount);
    }
}
