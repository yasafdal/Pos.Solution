using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class InventoryController : ControllerBase
    {
        private readonly PosDbContext _context;

        public InventoryController(PosDbContext context)
        {
            _context = context;
        }

        // GET: api/Inventory/branch/1
        [HttpGet("branch/{branchId}")]
        public async Task<ActionResult<IEnumerable<BranchInventoryDto>>> GetBranchInventory(int branchId)
        {
            // Ensure all products exist in BranchInventory for this branch
            var existingProductIds = await _context.BranchInventories
                .Where(bi => bi.BranchId == branchId)
                .Select(bi => bi.ProductId)
                .ToListAsync();

            var missingProducts = await _context.Products
                .Where(p => !existingProductIds.Contains(p.ProductId))
                .ToListAsync();

            if (missingProducts.Any())
            {
                var newInventoryEntries = missingProducts.Select(p => new BranchInventory
                {
                    BranchId = branchId,
                    ProductId = p.ProductId,
                    StockQuantity = 0,
                    ReorderLevel = 5,
                    LastUpdated = DateTime.UtcNow
                });

                _context.BranchInventories.AddRange(newInventoryEntries);
                await _context.SaveChangesAsync();
            }

            var inventory = await (from p in _context.Products
                                   join bi in _context.BranchInventories.Where(x => x.BranchId == branchId)
                                   on p.ProductId equals bi.ProductId into biJoin
                                   from subBi in biJoin.DefaultIfEmpty()
                                   join c in _context.Categories on p.CategoryId equals c.CategoryId into cJoin
                                   from subC in cJoin.DefaultIfEmpty()
                                   where !p.IsDeleted
                                   select new BranchInventoryDto
                                   {
                                       BranchInventoryId = subBi != null ? subBi.BranchInventoryId : 0,
                                       BranchId = branchId,
                                       ProductId = p.ProductId,
                                       ProductName = p.Name,
                                       CategoryName = subC != null ? subC.Name : "Uncategorized",
                                       ImageUrl = p.ImageUrl,
                                       Price = p.Price,
                                       StockQuantity = subBi != null ? subBi.StockQuantity : 0,
                                       ReorderLevel = subBi != null ? subBi.ReorderLevel : 5,
                                       LastUpdated = subBi != null ? subBi.LastUpdated : DateTime.UtcNow
                                   }).ToListAsync();

            return Ok(inventory);
        }

        // POST: api/Inventory/adjust
        [HttpPost("adjust")]
        public async Task<IActionResult> AdjustStock([FromBody] StockAdjustmentDto dto)
        {
            var item = await _context.BranchInventories
                .FirstOrDefaultAsync(bi => bi.BranchId == dto.BranchId && bi.ProductId == dto.ProductId);

            if (item == null)
            {
                item = new BranchInventory
                {
                    BranchId = dto.BranchId,
                    ProductId = dto.ProductId,
                    StockQuantity = 0,
                    ReorderLevel = dto.ReorderLevel,
                    LastUpdated = DateTime.UtcNow
                };
                _context.BranchInventories.Add(item);
            }

            int previousQuantity = item.StockQuantity;

            switch (dto.AdjustmentType.ToLower())
            {
                case "add":
                    item.StockQuantity += dto.Quantity;
                    break;
                case "deduct":
                    item.StockQuantity = Math.Max(0, item.StockQuantity - dto.Quantity);
                    break;
                case "set":
                    item.StockQuantity = Math.Max(0, dto.Quantity);
                    break;
            }

            item.ReorderLevel = dto.ReorderLevel;
            item.LastUpdated = DateTime.UtcNow;

            // Log the adjustment with Reason/Notes into the audit trail table
            var log = new StockAdjustment
            {
                BranchId = dto.BranchId,
                ProductId = dto.ProductId,
                AdjustmentType = dto.AdjustmentType,
                Quantity = dto.Quantity,
                PreviousQuantity = previousQuantity,
                NewQuantity = item.StockQuantity,
                Reason = dto.Reason,
                CreatedAt = DateTime.UtcNow
            };

            _context.StockAdjustments.Add(log);

            await _context.SaveChangesAsync();
            return Ok(new { Message = "Stock updated successfully", NewStock = item.StockQuantity });
        }
    }
}
