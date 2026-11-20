using POS.Shared.Models;

namespace POS.Client.Services
{
    public class CartService : IDisposable
    {
        private readonly List<CartItem> _items = new();
        private readonly PosSessionService session;

        public CartService(PosSessionService session)
        {
            this.session = session;
            session.OnChange += Clear;
        }

        public void Dispose() => session.OnChange -= Clear;

        public IReadOnlyList<CartItem> Items => _items.AsReadOnly();

        public decimal Subtotal => _items.Sum(i => i.LineTotal);
        public decimal TotalAmount => System.Math.Max(0m, activeCalculatedTotal ?? (Subtotal - activeDiscountAmount));
        public decimal Total => TotalAmount;
        public decimal TotalItems => _items.Sum(i => i.Quantity);

        public event Action? OnChange;
        private string activeDiscountCode = "NONE";
        private decimal activeDiscountAmount = 0.00m;
        private string? activeDiscountMethod;
        private decimal? activeDiscountValue;
        private decimal manualDiscountPercentage = 0.00m;
        private decimal manualDiscountAmount = 0.00m;
        private decimal promoDiscountAmount = 0.00m;
        private string activeBranchTaxType = "VAT-Registered";
        private decimal? activeCalculatedTotal;
        private string? customerCardNumber;

        public string ActiveDiscountCode => activeDiscountCode;
        public decimal ActiveDiscountAmount => activeDiscountAmount;
        public string? ActiveDiscountMethod => activeDiscountMethod;
        public decimal? ActiveDiscountValue => activeDiscountValue;
        public decimal ManualDiscountPercentage => manualDiscountPercentage;
        public decimal ManualDiscountAmount => manualDiscountAmount;
        public decimal PromoDiscountAmount => promoDiscountAmount;
        public string? CustomerCardNumber => customerCardNumber;

        public void ApplyDiscount(string code, decimal amount, string? cardNumber = null)
        {
            ApplyDiscount(code, amount, cardNumber, null, null, 0m, 0m, 0m, null, null);
        }

        public void ApplyDiscount(
            string code,
            decimal amount,
            string? cardNumber,
            string? discountMethod,
            decimal? discountValue,
            decimal manualPercentage = 0m,
            decimal manualAmount = 0m,
            decimal promoAmount = 0m,
            string? branchTaxType = null,
            decimal? calculatedTotal = null)
        {
            if (amount < 0m)
                throw new ArgumentOutOfRangeException(nameof(amount), "Invalid discount value: discount amount cannot be negative.");
            if (manualPercentage < 0m || manualPercentage > POS.Shared.Services.LocalDiscountCalculationService.MaxPercentageLimit)
                throw new ArgumentOutOfRangeException(nameof(manualPercentage), "Discount percentage cannot exceed the allowed limit (0% to 100%).");
            if (manualAmount < 0m)
                throw new ArgumentOutOfRangeException(nameof(manualAmount), "Invalid discount value: discount amount cannot be negative.");
            if (promoAmount < 0m)
                throw new ArgumentOutOfRangeException(nameof(promoAmount), "Invalid discount value: promotional discount amount cannot be negative.");

            decimal subtotal = Subtotal;
            string normalizedCode = string.IsNullOrWhiteSpace(code) ? "NONE" : code.Trim().ToUpperInvariant();
            if (normalizedCode == "NONE")
            {
                ClearDiscount();
                return;
            }

            if (amount > subtotal + 0.01m)
                throw new ArgumentOutOfRangeException(nameof(amount), "Discount amount cannot exceed the eligible amount.");

            activeDiscountCode = normalizedCode;
            activeDiscountAmount = System.Math.Min(subtotal, System.Math.Round(amount, 2));
            activeDiscountMethod = string.IsNullOrWhiteSpace(discountMethod) ? null : discountMethod.Trim().ToUpperInvariant();
            activeDiscountValue = discountValue;
            manualDiscountPercentage = manualPercentage;
            manualDiscountAmount = manualAmount;
            promoDiscountAmount = promoAmount;
            if (!string.IsNullOrWhiteSpace(branchTaxType))
                activeBranchTaxType = branchTaxType.Trim();
            activeCalculatedTotal = calculatedTotal.HasValue
                ? System.Math.Max(0m, System.Math.Round(calculatedTotal.Value, 2))
                : System.Math.Max(0m, System.Math.Round(subtotal - activeDiscountAmount, 2));
            customerCardNumber = cardNumber;
            NotifyStateChanged();
        }

        public OrderCalculationRequestDto BuildCalculationRequest(string? branchTaxType = null)
        {
            string resolvedTaxType = !string.IsNullOrWhiteSpace(branchTaxType)
                ? branchTaxType.Trim()
                : (!string.IsNullOrWhiteSpace(session.Terminal?.BranchTaxType) ? session.Terminal!.BranchTaxType : activeBranchTaxType);

            decimal resolvedPromo = promoDiscountAmount;
            if (resolvedPromo == 0m
                && activeDiscountCode != "NONE"
                && activeDiscountCode != "MANUAL"
                && activeDiscountCode != "EMPLOYEE"
                && !POS.Shared.Services.LocalDiscountCalculationService.IsStatutoryCode(activeDiscountCode)
                && string.IsNullOrWhiteSpace(activeDiscountMethod)
                && !activeDiscountValue.HasValue
                && activeDiscountAmount > 0m)
            {
                resolvedPromo = activeDiscountAmount;
            }

            return new OrderCalculationRequestDto
            {
                BranchTaxType = resolvedTaxType,
                DiscountCode = activeDiscountCode,
                DiscountMethod = activeDiscountMethod,
                DiscountValue = activeDiscountValue,
                ManualDiscountPercentage = manualDiscountPercentage,
                ManualDiscountAmount = manualDiscountAmount,
                PromoDiscountAmount = resolvedPromo,
                Items = _items.Select(ci => new CartItemCalculationDto
                {
                    ProductId = ci.ProductId,
                    ProductName = ci.ProductName,
                    Quantity = ci.Quantity,
                    UnitPrice = ci.IsComplimentary ? 0m : ci.BasePrice,
                    TargetAmount = ci.IsComplimentary ? 0m : ci.TargetAmount,
                    ModifierTotal = ci.IsComplimentary ? 0m : ci.SelectedModifiers.Sum(m => m.Price),
                    IsComplimentary = ci.IsComplimentary,
                    TaxName = ci.TaxName ?? "VATable",
                    IsEligibleForStatutoryDiscount = true
                }).ToList()
            };
        }

        private void ClearDiscountInternal()
        {
            activeDiscountCode = "NONE";
            activeDiscountAmount = 0.00m;
            activeDiscountMethod = null;
            activeDiscountValue = null;
            manualDiscountPercentage = 0.00m;
            manualDiscountAmount = 0.00m;
            promoDiscountAmount = 0.00m;
            activeCalculatedTotal = null;
            customerCardNumber = null;
        }

        public void ClearDiscount()
        {
            ClearDiscountInternal();
            NotifyStateChanged();
        }

        private void RecalculateActiveDiscount()
        {
            if (_items.Count == 0)
            {
                ClearDiscountInternal();
                return;
            }

            if (activeDiscountCode == "NONE")
            {
                activeDiscountAmount = 0.00m;
                activeCalculatedTotal = null;
                return;
            }

            decimal subtotal = Subtotal;
            if (subtotal <= 0m)
            {
                activeDiscountAmount = 0.00m;
                activeCalculatedTotal = 0.00m;
                return;
            }

            // Cap fixed amounts to the updated eligible subtotal if the cashier reduced quantities or removed items
            if (manualDiscountAmount > subtotal)
                manualDiscountAmount = subtotal;
            if (promoDiscountAmount > subtotal)
                promoDiscountAmount = subtotal;
            if (activeDiscountMethod == "FIXED" && activeDiscountValue.HasValue && activeDiscountValue.Value > subtotal)
                activeDiscountValue = subtotal;
            if (activeDiscountAmount > subtotal)
                activeDiscountAmount = subtotal;

            try
            {
                var req = BuildCalculationRequest();
                var calc = new POS.Shared.Services.LocalDiscountCalculationService().CalculateOrder(req);
                activeDiscountAmount = System.Math.Min(subtotal, System.Math.Max(0m, calc.DiscountAmount));
                activeCalculatedTotal = System.Math.Max(0m, calc.TotalAmount);
            }
            catch
            {
                activeDiscountAmount = System.Math.Min(subtotal, System.Math.Max(0m, activeDiscountAmount));
                activeCalculatedTotal = System.Math.Max(0m, System.Math.Round(subtotal - activeDiscountAmount, 2));
            }
        }

        private static bool ModifiersEqual(List<ModifierOptionDto> a, List<ModifierOptionDto> b)
        {
            var list1 = a ?? new();
            var list2 = b ?? new();
            if (list1.Count != list2.Count) return false;
            var ids1 = list1.Select(x => x.ModifierOptionId).OrderBy(id => id).ToList();
            var ids2 = list2.Select(x => x.ModifierOptionId).OrderBy(id => id).ToList();
            return ids1.SequenceEqual(ids2);
        }

        private static bool NotesEqual(string? a, string? b)
        {
            var normA = CartItem.NormalizeSpecialInstructions(a);
            var normB = CartItem.NormalizeSpecialInstructions(b);
            return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
        }

        public void AddItem(ProductDto product, int quantity = 1)
        {
            AddItem(product, quantity, null, null);
        }

        public void AddItem(ProductDto product, int quantity, List<ModifierOptionDto>? selectedModifiers, string? specialInstructions = null)
        {
            selectedModifiers ??= new();
            if (quantity <= 0 || (decimal)quantity > (decimal)int.MaxValue - TotalItems)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive and fit the current order.");

            var normalizedInstructions = CartItem.NormalizeSpecialInstructions(specialInstructions);
            var existing = _items.FirstOrDefault(i =>
                i.ProductId == product.ProductId &&
                ModifiersEqual(i.SelectedModifiers, selectedModifiers) &&
                NotesEqual(i.SpecialInstructions, normalizedInstructions));

            if (existing != null)
            {
                existing.Quantity += quantity;
            }
            else
            {
                _items.Add(new CartItem
                {
                    CartItemId = Guid.NewGuid().ToString(),
                    ProductId = product.ProductId,
                    ProductName = product.Name,
                    ProductShortCode = product.ShortCode,
                    BasePrice = product.Price,
                    SelectedModifiers = selectedModifiers,
                    Quantity = quantity,
                    ImageUrl = product.ImageUrl,
                    UnitOfMeasure = product.UnitOfMeasure,
                    TaxName = product.TaxName ?? "VATable",
                    IsKitchenItem = product.IsKitchenItem,
                    IsBarItem = product.IsBarItem,
                    SpecialInstructions = normalizedInstructions
                });
            }
            RecalculateActiveDiscount();
            NotifyStateChanged();
        }

        public void AddWeightBasedItem(
            ProductDto product,
            decimal weight,
            decimal? targetAmount,
            List<ModifierOptionDto>? selectedModifiers = null,
            string? specialInstructions = null,
            List<CartItem>? complimentaryItems = null)
        {
            if (weight <= 0m)
                throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be positive.");

            var normalizedInstructions = CartItem.NormalizeSpecialInstructions(specialInstructions);
            var parentItem = new CartItem
            {
                CartItemId = Guid.NewGuid().ToString(),
                ProductId = product.ProductId,
                ProductName = product.Name,
                ProductShortCode = product.ShortCode,
                BasePrice = product.Price,
                PricingMethod = product.PricingMethod,
                Quantity = weight,
                TargetAmount = targetAmount,
                SelectedModifiers = selectedModifiers ?? new(),
                ImageUrl = product.ImageUrl,
                UnitOfMeasure = product.UnitOfMeasure ?? "kg",
                TaxName = product.TaxName ?? "VATable",
                IsKitchenItem = product.IsKitchenItem,
                IsBarItem = product.IsBarItem,
                SpecialInstructions = normalizedInstructions
            };

            _items.Add(parentItem);

            if (complimentaryItems != null && complimentaryItems.Count > 0)
            {
                foreach (var comp in complimentaryItems)
                {
                    comp.ParentCartItemId = parentItem.CartItemId;
                    comp.ParentProductId = parentItem.ProductId;
                    comp.IsComplimentary = true;
                    comp.BasePrice = 0m;
                    _items.Add(comp);
                }
            }

            RecalculateActiveDiscount();
            NotifyStateChanged();
        }

        public void RestoreHeldSale(
            HeldSaleDto heldSale,
            IReadOnlyDictionary<int, (bool IsKitchenItem, bool IsBarItem, string? UnitOfMeasure, string? PricingMethod)>? catalogLookup = null)
        {
            ArgumentNullException.ThrowIfNull(heldSale);
            _items.Clear();
            ClearDiscountInternal();

            string? lastWeightParentCartItemId = null;
            int? lastWeightParentProductId = null;

            foreach (var item in heldSale.Items ?? new())
            {
                if (item.Quantity <= 0m) continue;

                (bool IsKitchenItem, bool IsBarItem, string? UnitOfMeasure, string? PricingMethod) meta = default;
                bool hasMeta = catalogLookup != null && catalogLookup.TryGetValue(item.ProductId, out meta);

                var mods = (item.Modifiers ?? new()).Select(m => new ModifierOptionDto
                {
                    ModifierOptionId = m.ModifierOptionId,
                    ProductId = m.ProductId,
                    ProductName = m.ProductName,
                    Name = m.ModifierName,
                    Price = m.AdditionalPrice
                }).ToList();

                string? resolvedPricing = !string.IsNullOrWhiteSpace(item.PricingMethod)
                    ? item.PricingMethod
                    : (hasMeta ? meta.PricingMethod : null);
                string? resolvedUom = !string.IsNullOrWhiteSpace(item.UnitOfMeasure)
                    ? item.UnitOfMeasure
                    : (hasMeta ? meta.UnitOfMeasure : null);
                bool isKitchen = item.IsKitchenItem || (hasMeta && meta.IsKitchenItem);
                bool isBar = item.IsBarItem || (hasMeta && meta.IsBarItem);

                var cartItem = new CartItem
                {
                    CartItemId = Guid.NewGuid().ToString(),
                    ProductId = item.ProductId,
                    ProductName = item.ProductName,
                    ProductShortCode = item.ProductShortCode,
                    BasePrice = item.IsComplimentary ? 0m : item.BasePrice,
                    PricingMethod = resolvedPricing,
                    UnitOfMeasure = resolvedUom ?? (string.Equals(resolvedPricing, "WeightBased", StringComparison.OrdinalIgnoreCase) ? "kg" : null),
                    Quantity = item.Quantity,
                    TargetAmount = item.IsComplimentary ? null : item.TargetAmount,
                    IsComplimentary = item.IsComplimentary,
                    ParentProductId = item.ParentProductId,
                    SelectedModifiers = mods,
                    TaxName = string.IsNullOrWhiteSpace(item.TaxName) ? "VATable" : item.TaxName,
                    IsKitchenItem = isKitchen,
                    IsBarItem = isBar,
                    SpecialInstructions = CartItem.NormalizeSpecialInstructions(item.SpecialInstructions)
                };

                if (cartItem.IsWeightBased && !cartItem.IsComplimentary)
                {
                    lastWeightParentCartItemId = cartItem.CartItemId;
                    lastWeightParentProductId = cartItem.ProductId;
                }
                else if (cartItem.IsComplimentary && lastWeightParentCartItemId != null &&
                         (!cartItem.ParentProductId.HasValue || cartItem.ParentProductId == lastWeightParentProductId))
                {
                    cartItem.ParentCartItemId = lastWeightParentCartItemId;
                    cartItem.ParentProductId ??= lastWeightParentProductId;
                }

                _items.Add(cartItem);
            }

            if (!string.IsNullOrWhiteSpace(heldSale.DiscountCode) && heldSale.DiscountCode != "NONE")
            {
                decimal restoredSubtotal = Subtotal;
                activeDiscountCode = heldSale.DiscountCode.Trim().ToUpperInvariant();
                activeDiscountAmount = System.Math.Min(restoredSubtotal, System.Math.Max(0m, System.Math.Round(heldSale.DiscountAmount, 2)));
                activeCalculatedTotal = System.Math.Max(0m, System.Math.Round(restoredSubtotal - activeDiscountAmount, 2));
                customerCardNumber = heldSale.CustomerCardNumber;
            }

            NotifyStateChanged();
        }

        public void UpdateSpecialInstructions(CartItem item, string? specialInstructions)
        {
            if (item == null || !_items.Contains(item))
                return;

            var normalizedInstructions = CartItem.NormalizeSpecialInstructions(specialInstructions);
            var matchingLine = (!item.IsWeightBased && !item.IsComplimentary)
                ? _items.FirstOrDefault(i =>
                    !ReferenceEquals(i, item) &&
                    !i.IsWeightBased &&
                    !i.IsComplimentary &&
                    i.ProductId == item.ProductId &&
                    ModifiersEqual(i.SelectedModifiers, item.SelectedModifiers) &&
                    NotesEqual(i.SpecialInstructions, normalizedInstructions))
                : null;

            if (matchingLine != null && matchingLine.Quantity <= int.MaxValue - item.Quantity)
            {
                matchingLine.Quantity += item.Quantity;
                _items.Remove(item);
            }
            else
            {
                item.SpecialInstructions = normalizedInstructions;
            }
            NotifyStateChanged();
        }

        public void IncreaseQuantity(int productId)
        {
            var item = _items.FirstOrDefault(i => i.ProductId == productId);
            if (item != null && TotalItems < int.MaxValue)
            {
                item.Quantity++;
                RecalculateActiveDiscount();
                NotifyStateChanged();
            }
        }

        public void IncreaseQuantity(CartItem item)
        {
            if (item != null && _items.Contains(item) && TotalItems < int.MaxValue)
            {
                item.Quantity++;
                RecalculateActiveDiscount();
                NotifyStateChanged();
            }
        }

        public void DecreaseQuantity(int productId)
        {
            var item = _items.FirstOrDefault(i => i.ProductId == productId);
            if (item != null)
            {
                if (item.Quantity > 1)
                {
                    item.Quantity--;
                }
                else
                {
                    _items.Remove(item);
                }
                RecalculateActiveDiscount();
                NotifyStateChanged();
            }
        }

        public void DecreaseQuantity(CartItem item)
        {
            if (item != null && _items.Contains(item))
            {
                if (item.Quantity > 1)
                {
                    item.Quantity--;
                }
                else
                {
                    _items.Remove(item);
                }
                RecalculateActiveDiscount();
                NotifyStateChanged();
            }
        }

        public void RemoveItem(int productId)
        {
            var item = _items.FirstOrDefault(i => i.ProductId == productId);
            if (item != null)
            {
                RemoveItem(item);
            }
        }

        public void RemoveItem(CartItem item)
        {
            if (item != null && _items.Contains(item))
            {
                var cartId = item.CartItemId;
                var prodId = item.ProductId;
                _items.Remove(item);
                if (!item.IsComplimentary)
                {
                    // Fallback on ParentProductId only for legacy/orphaned complimentary rows lacking ParentCartItemId.
                    _items.RemoveAll(i => i.IsComplimentary && (
                        (!string.IsNullOrEmpty(cartId) && i.ParentCartItemId == cartId) ||
                        (string.IsNullOrEmpty(i.ParentCartItemId) && prodId > 0 && i.ParentProductId == prodId)));
                }
                RecalculateActiveDiscount();
                NotifyStateChanged();
            }
        }

        public void Clear()
        {
            _items.Clear();
            ClearDiscount();
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}
