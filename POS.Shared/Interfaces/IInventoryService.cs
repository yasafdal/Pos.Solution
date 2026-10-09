using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface IInventoryService
{
    Task<List<BranchInventoryDto>> GetBranchInventoryAsync(int branchId);
    Task<bool> AdjustStockAsync(StockAdjustmentDto dto);
}



