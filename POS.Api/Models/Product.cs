using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class Product
{
    public int ProductId { get; set; }

    public int CategoryId { get; set; }

    public string Name { get; set; } = null!;

    public decimal? UnitValue { get; set; }

    public string? UnitOfMeasure { get; set; }

    public string? Barcode { get; set; }

    public decimal Price { get; set; }

    public decimal? CostPrice { get; set; }

    public int StockQuantity { get; set; }

    public string? Sku { get; set; }

    public int? TaxId { get; set; }

    public int ReorderLevel { get; set; }

    public bool IsActive { get; set; }

    public string? ImageUrl { get; set; }

    public string ProductType { get; set; } = null!;

    public bool IsKitchenItem { get; set; }

    public bool IsDeleted { get; set; }

    public virtual ICollection<BranchInventory> BranchInventories { get; set; } = new List<BranchInventory>();

    public virtual Category Category { get; set; } = null!;

    public virtual ICollection<ProductRecipe> ProductRecipeProducts { get; set; } = new List<ProductRecipe>();

    public virtual ICollection<ProductRecipe> ProductRecipeRawMaterialProducts { get; set; } = new List<ProductRecipe>();

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();

    public virtual Taxis? Tax { get; set; }
}
