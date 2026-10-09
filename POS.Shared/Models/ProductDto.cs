using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models;

public class ProductDto
{
    public int ProductId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public decimal? UnitValue { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? Barcode { get; set; }
    public string? SKU { get; set; }
    public string? ImageUrl { get; set; }

    public decimal Price { get; set; }
    public decimal CostPrice { get; set; } 

    public int? TaxId { get; set; }
    public string? TaxName { get; set; } 
    public string ProductType { get; set; } = string.Empty;

    public bool IsActive { get; set; } 
    public bool IsKitchenItem { get; set; }
}
