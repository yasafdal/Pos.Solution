using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class Branch
{
    public int BranchId { get; set; }

    public string Name { get; set; } = null!;

    public string? Code { get; set; }

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public bool IsActive { get; set; }

    public string TaxType { get; set; } = null!;

    public virtual ICollection<BranchInventory> BranchInventories { get; set; } = new List<BranchInventory>();

    public virtual ICollection<Outlet> Outlets { get; set; } = new List<Outlet>();

    public virtual ICollection<StockAdjustment> StockAdjustments { get; set; } = new List<StockAdjustment>();
}
