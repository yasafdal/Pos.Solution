using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace POS.Api.Models;

public partial class HeldSale
{
    [Key]
    public long HeldSaleId { get; set; }

    public int BranchId { get; set; }

    public int TerminalId { get; set; }

    [Required]
    [MaxLength(450)]
    public string CashierId { get; set; } = null!;

    [Required]
    [MaxLength(50)]
    public string HoldReference { get; set; } = null!;

    [MaxLength(200)]
    public string? CustomerName { get; set; }

    [MaxLength(255)]
    public string? Notes { get; set; }

    public decimal Subtotal { get; set; }

    public decimal DiscountAmount { get; set; }

    [MaxLength(30)]
    public string? DiscountCode { get; set; }

    [MaxLength(50)]
    public string? CustomerCardNumber { get; set; }

    public decimal TotalAmount { get; set; }

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "HELD"; // HELD, RESUMED, CANCELLED, COMPLETED

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ResumedAt { get; set; }

    public virtual ICollection<HeldSaleItem> HeldSaleItems { get; set; } = new List<HeldSaleItem>();
}

public partial class HeldSaleItem
{
    [Key]
    public long HeldSaleItemId { get; set; }

    public long HeldSaleId { get; set; }

    public int ProductId { get; set; }

    [Required]
    [MaxLength(200)]
    public string ProductName { get; set; } = null!;

    [MaxLength(50)]
    public string? ProductShortCode { get; set; }

    public decimal Quantity { get; set; }

    public decimal BasePrice { get; set; }

    public decimal LineTotal { get; set; }

    [Required]
    [MaxLength(50)]
    public string TaxName { get; set; } = "VATable";

    [ForeignKey("HeldSaleId")]
    public virtual HeldSale? HeldSale { get; set; }

    public virtual ICollection<HeldSaleItemModifier> HeldSaleItemModifiers { get; set; } = new List<HeldSaleItemModifier>();
}

public partial class HeldSaleItemModifier
{
    [Key]
    public long HeldSaleItemModifierId { get; set; }

    public long HeldSaleItemId { get; set; }

    public int ModifierOptionId { get; set; }

    [Required]
    [MaxLength(100)]
    public string ModifierName { get; set; } = null!;

    public decimal AdditionalPrice { get; set; }

    [ForeignKey("HeldSaleItemId")]
    public virtual HeldSaleItem? HeldSaleItem { get; set; }
}
