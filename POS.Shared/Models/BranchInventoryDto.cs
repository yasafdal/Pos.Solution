using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models
{
    public class BranchInventoryDto
    {
        public int BranchInventoryId { get; set; }
        public int BranchId { get; set; }
        public string BranchName { get; set; } = string.Empty;
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;
        public string? CategoryName { get; set; }
        public string? ImageUrl { get; set; }
        public decimal Price { get; set; }
        public int StockQuantity { get; set; }
        public int ReorderLevel { get; set; }
        public DateTime LastUpdated { get; set; } = DateTime.Now;
        public bool IsLowStock => StockQuantity <= ReorderLevel;
    }
}
