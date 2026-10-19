using System;
using System.Collections.Generic;

namespace POS.Shared.Models;

public class HeldSaleCreateDto
{
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = null!;
    public string? CustomerName { get; set; }
    public string? Notes { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountCode { get; set; }
    public string? CustomerCardNumber { get; set; }
    public decimal TotalAmount { get; set; }
    public List<HeldSaleItemDto> Items { get; set; } = new();
}

public class HeldSaleDto
{
    public long HeldSaleId { get; set; }
    public int BranchId { get; set; }
    public int OutletId { get; set; }
    public string CashierId { get; set; } = null!;
    public string HoldReference { get; set; } = null!;
    public string? CustomerName { get; set; }
    public string? Notes { get; set; }
    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public string? DiscountCode { get; set; }
    public string? CustomerCardNumber { get; set; }
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = "HELD";
    public DateTime CreatedAt { get; set; }
    public DateTime? ResumedAt { get; set; }
    public List<HeldSaleItemDto> Items { get; set; } = new();
}

public class HeldSaleItemDto
{
    public long HeldSaleItemId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = null!;
    public string? ProductShortCode { get; set; }
    public decimal Quantity { get; set; }
    public decimal BasePrice { get; set; }
    public decimal LineTotal { get; set; }
    public string TaxName { get; set; } = "VATable";
    public List<HeldSaleModifierDto> Modifiers { get; set; } = new();
}

public class HeldSaleModifierDto
{
    public long HeldSaleItemModifierId { get; set; }
    public int ModifierOptionId { get; set; }
    public string ModifierName { get; set; } = null!;
    public decimal AdditionalPrice { get; set; }
}
