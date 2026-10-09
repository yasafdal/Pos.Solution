using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models
{
    public class ProductRecipeDto
    {
        public int RecipeId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; } = string.Empty;

        public int RawMaterialProductId { get; set; }
        public string RawMaterialName { get; set; } = string.Empty;

        public decimal QuantityRequired { get; set; }
        public string? Uom { get; set; }
    }
}
