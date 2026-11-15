using System.Globalization;
using POS.Shared.Constants;

namespace POS.Shared.Models
{
    public class CartItem
    {
        public string CartItemId { get; set; } = System.Guid.NewGuid().ToString();
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string? ProductShortCode { get; set; }
        public decimal BasePrice { get; set; }
        public List<ModifierOptionDto> SelectedModifiers { get; set; } = new();
        public decimal UnitPrice => IsComplimentary ? 0m : (BasePrice + (SelectedModifiers?.Sum(m => m.Price) ?? 0m));
        public decimal Price 
        { 
            get => UnitPrice; 
            set => BasePrice = value; 
        }
        public decimal Quantity { get; set; } = 1m;
        public string PricingMethod { get; set; } = POS.Shared.Constants.PricingMethods.Fixed;
        public bool IsWeightBased => POS.Shared.Constants.PricingMethods.IsWeightBased(PricingMethod);
        public decimal? TargetAmount { get; set; }
        public bool IsComplimentary { get; set; } = false;
        public int? ParentProductId { get; set; }
        public string? ParentCartItemId { get; set; }
        public string? ImageUrl { get; set; }
        public string? UnitOfMeasure { get; set; }
        public string? TaxName { get; set; } = "VATable";
        public bool IsKitchenItem { get; set; }
        public bool IsBarItem { get; set; }
        public const int MaxSpecialInstructionsLength = 250;
        public string? SpecialInstructions { get; set; }
        public decimal LineTotal => IsComplimentary
            ? 0m
            : (TargetAmount.HasValue && TargetAmount.Value > 0
                ? TargetAmount.Value
                : System.Math.Round(UnitPrice * Quantity, 2, System.MidpointRounding.AwayFromZero));

        public static string? NormalizeSpecialInstructions(string? instructions)
        {
            if (string.IsNullOrWhiteSpace(instructions))
                return null;

            var trimmed = instructions.Trim();
            return trimmed.Length > MaxSpecialInstructionsLength
                ? trimmed[..MaxSpecialInstructionsLength].TrimEnd()
                : trimmed;
        }

        /// <summary>
        /// Formats a cart quantity with up to 3 decimal places using invariant culture,
        /// preserving tiny positive values as "&lt;0.001" so they never display as "0".
        /// </summary>
        public static string FormatQuantity(decimal quantity)
        {
            var rounded = Math.Round(quantity, 3, MidpointRounding.AwayFromZero);
            if (quantity > 0m && rounded == 0m)
                return "<0.001";

            return rounded.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a product card badge quantity, appending " kg" for weight-based items.
        /// </summary>
        public static string FormatProductCardQuantity(decimal quantity, string? pricingMethod)
        {
            var formatted = FormatQuantity(quantity);
            return PricingMethods.IsWeightBased(pricingMethod) ? $"{formatted} kg" : formatted;
        }
    }
}
