using System;
using System.Collections.Generic;
using System.Linq;

namespace POS.Shared.Models;

public class CartItemCalculationDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; } // Inclusive of VAT for VAT-registered, or regular price for Non-VAT
    public decimal ModifierTotal { get; set; }
    public int? TaxId { get; set; }
    public string TaxName { get; set; } = "VATable"; // VATable, VAT-Exempt, Zero-Rated
    public decimal TaxRate { get; set; } = 12.00m;
    public bool IsEligibleForStatutoryDiscount { get; set; } = true; // e.g. food/medicine eligible for SC/PWD
}

public class OrderCalculationRequestDto
{
    public string BranchTaxType { get; set; } = "VAT-Registered"; // VAT-Registered or Non-VAT
    public string DiscountCode { get; set; } = "NONE"; // NONE, SC, PWD, BNPC, PROMO, EMPLOYEE, MANUAL
    public decimal ManualDiscountPercentage { get; set; } = 0.00m;
    public decimal PromoDiscountAmount { get; set; } = 0.00m;
    public List<CartItemCalculationDto> Items { get; set; } = new();
}

public class OrderCalculationResultDto
{
    public decimal Subtotal { get; set; }
    public decimal VatableSales { get; set; }
    public decimal VatExemptSales { get; set; }
    public decimal ZeroRatedSales { get; set; }
    public decimal VatAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal TotalAmount { get; set; }
    public List<CalculatedItemDto> CalculatedItems { get; set; } = new();
}

public class CalculatedItemDto
{
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal OriginalUnitPrice { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal LineTotal { get; set; }
    public string VatType { get; set; } = "VATABLE";
    public decimal VatAmount { get; set; }
}
