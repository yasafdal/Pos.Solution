using System;
using System.Collections.Generic;

namespace POS.Api.Models;

public partial class StockAdjustment
{
    public int StockAdjustmentId { get; set; }

    public int BranchId { get; set; }

    public int ProductId { get; set; }

    public string AdjustmentType { get; set; } = null!;

    public int Quantity { get; set; }

    public int PreviousQuantity { get; set; }

    public int NewQuantity { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }

    public virtual Branch Branch { get; set; } = null!;

    public virtual Product Product { get; set; } = null!;
}
