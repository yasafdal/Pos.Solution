using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProductsController : ControllerBase
    {
        private readonly PosDbContext _context;

        public ProductsController(PosDbContext context)
        {
            _context = context;
        }

        // GET: api/Products
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ProductDto>>> GetProducts()
        {
            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.Tax)
                .Select(p => new ProductDto
                {
                    ProductId = p.ProductId,
                    CategoryId = p.CategoryId,
                    CategoryName = p.Category != null ? p.Category.Name : string.Empty,
                    Name = p.Name,
                    UnitValue = p.UnitValue,
                    UnitOfMeasure = p.UnitOfMeasure,
                    Barcode = p.Barcode,
                    SKU = p.Sku,
                    ImageUrl = p.ImageUrl,
                    Price = p.Price,
                    CostPrice = p.CostPrice.Value,
                    StockQuantity = p.StockQuantity,
                    ReorderLevel = p.ReorderLevel,
                    TaxId = p.TaxId,
                    TaxName = p.Tax != null ? p.Tax.Name : "No Tax",
                    ProductType = p.ProductType,
                    IsActive = p.IsActive,
                    IsKitchenItem = p.IsKitchenItem
                })
                .ToListAsync();

            return Ok(products);
        }

        // POST: api/Products/create-product
        [HttpPost("create-product")]
        public async Task<ActionResult<ProductDto>> CreateProduct(ProductDto dto)
        {
            var product = new Product
            {
                CategoryId = dto.CategoryId,
                Name = dto.Name,
                UnitValue = dto.UnitValue,
                UnitOfMeasure = dto.UnitOfMeasure,
                Barcode = dto.Barcode,
                Sku = dto.SKU,
                ImageUrl = dto.ImageUrl,
                Price = dto.Price,
                CostPrice = dto.CostPrice,
                StockQuantity = dto.StockQuantity,
                ReorderLevel = dto.ReorderLevel,
                TaxId = dto.TaxId,
                ProductType = dto.ProductType,
                IsActive = dto.IsActive,
                IsKitchenItem = dto.IsKitchenItem
            };

            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            dto.ProductId = product.ProductId;
            return CreatedAtAction(nameof(GetProducts), new { id = product.ProductId }, dto);
        }

        // PUT: api/Products/update-product/5
        [HttpPut("update-product/{id}")]
        public async Task<IActionResult> UpdateProduct(int id, ProductDto dto)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.CategoryId = dto.CategoryId;
            product.Name = dto.Name;
            product.UnitValue = dto.UnitValue;
            product.UnitOfMeasure = dto.UnitOfMeasure;
            product.Barcode = dto.Barcode;
            product.Sku = dto.SKU;
            product.ImageUrl = dto.ImageUrl;
            product.Price = dto.Price;
            product.CostPrice = dto.CostPrice;
            product.StockQuantity = dto.StockQuantity;
            product.ReorderLevel = dto.ReorderLevel;
            product.TaxId = dto.TaxId;
            product.ProductType = dto.ProductType;
            product.IsActive = dto.IsActive;
            product.IsKitchenItem = dto.IsKitchenItem;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/Products/remove-product/5
        [HttpDelete("remove-product/{id}")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            _context.Products.Remove(product);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
