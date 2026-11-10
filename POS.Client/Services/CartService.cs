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
        public decimal TotalAmount => System.Math.Max(0m, Subtotal - activeDiscountAmount);
        public decimal Total => TotalAmount;
        public int TotalItems => _items.Sum(i => i.Quantity);

        public event Action? OnChange;
        private string activeDiscountCode = "NONE";
        private decimal activeDiscountAmount = 0.00m;
        private string? customerCardNumber;

        public string ActiveDiscountCode => activeDiscountCode;
        public decimal ActiveDiscountAmount => activeDiscountAmount;
        public string? CustomerCardNumber => customerCardNumber;

        public void ApplyDiscount(string code, decimal amount, string? cardNumber = null)
        {
            activeDiscountCode = code;
            activeDiscountAmount = amount;
            customerCardNumber = cardNumber;
            NotifyStateChanged();
        }

        public void ClearDiscount()
        {
            activeDiscountCode = "NONE";
            activeDiscountAmount = 0.00m;
            customerCardNumber = null;
            NotifyStateChanged();
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
            if (quantity <= 0 || quantity > int.MaxValue - TotalItems)
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
            NotifyStateChanged();
        }

        public void UpdateSpecialInstructions(CartItem item, string? specialInstructions)
        {
            if (item == null || !_items.Contains(item))
                return;

            var normalizedInstructions = CartItem.NormalizeSpecialInstructions(specialInstructions);
            var matchingLine = _items.FirstOrDefault(i =>
                !ReferenceEquals(i, item) &&
                i.ProductId == item.ProductId &&
                ModifiersEqual(i.SelectedModifiers, item.SelectedModifiers) &&
                NotesEqual(i.SpecialInstructions, normalizedInstructions));

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
                NotifyStateChanged();
            }
        }

        public void IncreaseQuantity(CartItem item)
        {
            if (item != null && _items.Contains(item) && TotalItems < int.MaxValue)
            {
                item.Quantity++;
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
                NotifyStateChanged();
            }
        }

        public void RemoveItem(int productId)
        {
            var item = _items.FirstOrDefault(i => i.ProductId == productId);
            if (item != null)
            {
                _items.Remove(item);
                NotifyStateChanged();
            }
        }

        public void RemoveItem(CartItem item)
        {
            if (item != null && _items.Contains(item))
            {
                _items.Remove(item);
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
