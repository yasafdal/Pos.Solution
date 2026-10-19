using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class OfflineReferenceDataResponseDto
{
    public string Version { get; set; } = null!;
    public DateTime ServerTimestamp { get; set; }
    public OfflineOutletConfigDto Outlet { get; set; } = null!;
    public List<OfflineProductDto> Products { get; set; } = new();
    public List<OfflineCategoryDto> Categories { get; set; } = new();
    public List<OfflineModifierGroupDto> ModifierGroups { get; set; } = new();
    public List<OfflineProductModifierMappingDto> ProductModifierMappings { get; set; } = new();
    public List<OfflineTaxDto> Taxes { get; set; } = new();
    public List<OfflineDiscountDto> Discounts { get; set; } = new();
    public OfflineInvoiceSequenceDto? InvoiceSequence { get; set; }
    public List<OfflineCashierDto> Cashiers { get; set; } = new();
    public List<CashMovementCategoryDto> CashMovementCategories { get; set; } = new();
    public CashDrawerShiftDto? ActiveShift { get; set; }
}

public class OfflineCashierDto
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string PinHash { get; set; } = string.Empty;
}

public class OfflineInvoiceSequenceDto
{
    public int SequenceId { get; set; }
    public int OutletId { get; set; }
    public string SeriesCode { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public long StartingNumber { get; set; }
    public long EndingNumber { get; set; }
    public long LastIssuedNumber { get; set; }
    public int PaddingLength { get; set; } = 7;
    public bool IsActive { get; set; } = true;
}

public class OfflineProductModifierMappingDto
{
    public int ProductModifierGroupId { get; set; }
    public int ProductId { get; set; }
    public int ModifierGroupId { get; set; }
    public int DisplayOrder { get; set; }
}

public class OfflineOutletConfigDto
{
    public int OutletId { get; set; }
    public int BranchId { get; set; }
    public int? BusinessTypeId { get; set; }
    public string? BusinessType { get; set; }
    public string BranchName { get; set; } = string.Empty;
    public string? BranchCode { get; set; }
    public string? BranchAddress { get; set; }
    public string? BranchPhone { get; set; }
    public string OutletName { get; set; } = null!;
    public string BranchTaxType { get; set; } = null!;
    public string DeviceIdentifier { get; set; } = null!;
}

public class OfflineProductDto
{
    public int ProductId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public OfflineCategoryDto Category { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public decimal? UnitValue { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? Barcode { get; set; }
    public string? SKU { get; set; }
    public string? ImageUrl { get; set; }

    public decimal Price { get; set; }
    public decimal CostPrice { get; set; }

    public string? ShortCode { get; set; }

    public int? TaxId { get; set; }
    public string? TaxName { get; set; } = "VATable";
    public string ProductType { get; set; } = string.Empty;

    public bool IsActive { get; set; }
    public bool IsKitchenItem { get; set; }
}

public class OfflineCategoryDto
{
    public int CategoryId { get; set; }
    public string Name { get; set; } = null!;
    public string? ColorCode { get; set; }
}

public class OfflineModifierGroupDto
{
    public int ModifierGroupId { get; set; }
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public int MinSelections { get; set; }
    public int MaxSelections { get; set; } = 1;
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public List<OfflineModifierOptionDto> Options { get; set; } = new();
}

public class OfflineModifierOptionDto
{
    public int ModifierOptionId { get; set; }
    public int ModifierGroupId { get; set; }
    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int DisplayOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class OfflineTaxDto
{
    public int TaxId { get; set; }
    public string Name { get; set; } = null!;
    public decimal Rate { get; set; }
}

public class OfflineDiscountDto
{
    public int DiscountId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string DiscountCategory { get; set; } = null!;
    public string DiscountMethod { get; set; } = null!;
    public decimal DiscountValue { get; set; }
    public bool IsStatutory { get; set; }
    public bool RequiresIdentification { get; set; }
}
