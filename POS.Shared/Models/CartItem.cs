namespace POS.Shared.Models
{
    public class CartItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string? ProductShortCode { get; set; }
        public decimal BasePrice { get; set; }
        public List<ModifierOptionDto> SelectedModifiers { get; set; } = new();
        public decimal UnitPrice => BasePrice + (SelectedModifiers?.Sum(m => m.Price) ?? 0m);
        public decimal Price 
        { 
            get => UnitPrice; 
            set => BasePrice = value; 
        }
        public int Quantity { get; set; } = 1;
        public string? ImageUrl { get; set; }
        public string? UnitOfMeasure { get; set; }
        public string? TaxName { get; set; } = "VATable";
        public decimal LineTotal => UnitPrice * Quantity;
    }
}
