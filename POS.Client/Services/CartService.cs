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

        public void AddItem(ProductDto product, int quantity = 1)
        {
            if (quantity <= 0 || quantity > int.MaxValue - TotalItems)
                throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive and fit the current order.");
            var existing = _items.FirstOrDefault(i => i.ProductId == product.ProductId);
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
                    Price = product.Price,
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

        public void RemoveItem(int productId)
        {
            var item = _items.FirstOrDefault(i => i.ProductId == productId);
            if (item != null)
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
