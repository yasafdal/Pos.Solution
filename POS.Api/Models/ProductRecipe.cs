using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class ProductRecipe
{
    public int RecipeId { get; set; }

    public int ProductId { get; set; }

    public int RawMaterialProductId { get; set; }

    public decimal QuantityRequired { get; set; }

    public string? Uom { get; set; }

    public virtual Product Product { get; set; } = null!;

    public virtual Product RawMaterialProduct { get; set; } = null!;
}
