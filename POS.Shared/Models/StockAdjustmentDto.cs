using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models
{
    public class StockAdjustmentDto
    {
        public int BranchId { get; set; }
        public int ProductId { get; set; }

        // Adjustment Mode: "Add", "Deduct", "Set"
        public string AdjustmentType { get; set; } = "Add";
        public int Quantity { get; set; }
        public int ReorderLevel { get; set; } = 5;
        public string? Reason { get; set; }
    }
}
