namespace POS.Shared.Models
{
    public class CartItem
    {
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int Quantity { get; set; } = 1;
        public string? ImageUrl { get; set; }
        public string? UnitOfMeasure { get; set; }
        public decimal LineTotal => Price * Quantity;
    }
}
