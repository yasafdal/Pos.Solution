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
        public decimal Total => Subtotal;
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

        public void AddItem(ProductDto product, int quantity = 1)
        {
            AddItem(product, quantity, null);
        }

        public void AddItem(ProductDto product, int quantity, List<ModifierOptionDto>? selectedModifiers)
        {
            selectedModifiers ??= new();
            if (quantity <= 0 || quantity > int.MaxValue - TotalItems)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive and fit the current order.");
            var existing = _items.FirstOrDefault(i => i.ProductId == product.ProductId && ModifiersEqual(i.SelectedModifiers, selectedModifiers));
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
                    UnitOfMeasure = product.UnitOfMeasure
                });
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
            NotifyStateChanged();
        }

        private void NotifyStateChanged() => OnChange?.Invoke();
    }
}
