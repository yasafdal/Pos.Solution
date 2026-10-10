using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class BranchInventory
{
    public int BranchInventoryId { get; set; }

    public int BranchId { get; set; }

    public int ProductId { get; set; }

    public int StockQuantity { get; set; }

    public int ReorderLevel { get; set; }

    public DateTime LastUpdated { get; set; }

    public virtual Branch Branch { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
