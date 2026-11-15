using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ProductsController : ControllerBase
    {
        private readonly PosDbContext _context;
        private readonly IWebHostEnvironment _env;

        public ProductsController(PosDbContext context, IWebHostEnvironment env)
        {
            _context = context;
            _env = env;
        }

        // GET: api/Products
        [HttpGet]
        public async Task<ActionResult<IEnumerable<ProductDto>>> GetProducts()
        {
            var products = await _context.Products
                .Include(p => p.Category)
                .Include(p => p.Tax)
                .Where(p => p.IsDeleted == false)
                .OrderBy(p => p.CategoryId)
                .ThenBy(p => p.DisplayOrder)
                .ThenBy(p => p.ProductId)
                .Select(p => new ProductDto
                {
                    ProductId = p.ProductId,
                    CategoryId = p.CategoryId,
                    CategoryName = p.Category != null ? p.Category.Name : string.Empty,
                    Name = p.Name,
                    ShortCode = p.ShortCode,
                    UnitValue = p.UnitValue,
                    UnitOfMeasure = p.UnitOfMeasure,
                    Barcode = p.Barcode,
                    SKU = p.Sku,
                    ImageUrl = p.ImageUrl,
                    Price = p.Price,
                    CostPrice = p.CostPrice ?? 0m,
                    TaxId = p.TaxId,
                    TaxName = p.Tax != null ? p.Tax.Name : "No Tax",
                    ProductType = p.ProductType,
                    PricingMethod = p.PricingMethod ?? POS.Shared.Constants.PricingMethods.Fixed,
                    IsActive = p.IsActive,
                    IsKitchenItem = p.IsKitchenItem,
                    IsBarItem = p.IsBarItem,
                    DisplayOrder = p.DisplayOrder
                })
                .ToListAsync();

            return Ok(products);
        }

        // POST: api/Products/create-product
        [HttpPost("create-product")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<ActionResult<ProductDto>> CreateProduct(ProductDto dto)
        {
            int displayOrder = dto.DisplayOrder;
            if (displayOrder <= 0)
            {
                var maxOrder = await _context.Products
                    .Where(p => p.CategoryId == dto.CategoryId && !p.IsDeleted)
                    .MaxAsync(p => (int?)p.DisplayOrder) ?? 0;
                displayOrder = maxOrder + 1;
            }

            var product = new Product
            {
                CategoryId = dto.CategoryId,
                Name = dto.Name,
                ShortCode = dto.ShortCode,
                UnitValue = dto.UnitValue,
                UnitOfMeasure = dto.UnitOfMeasure,
                Barcode = dto.Barcode,
                Sku = dto.SKU,
                ImageUrl = dto.ImageUrl,
                Price = dto.Price,
                CostPrice = dto.CostPrice,
                TaxId = dto.TaxId,
                ProductType = dto.ProductType,
                PricingMethod = POS.Shared.Constants.PricingMethods.Normalize(dto.PricingMethod),
                IsActive = dto.IsActive,
                IsKitchenItem = dto.IsKitchenItem,
                IsBarItem = dto.IsBarItem,
                DisplayOrder = displayOrder
            };

            _context.Products.Add(product);
            await _context.SaveChangesAsync();

            dto.ProductId = product.ProductId;
            dto.DisplayOrder = product.DisplayOrder;
            return CreatedAtAction(nameof(GetProducts), new { id = product.ProductId }, dto);
        }

        // PUT: api/Products/update-product/5
        [HttpPut("update-product/{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UpdateProduct(int id, ProductDto dto)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.CategoryId = dto.CategoryId;
            product.Name = dto.Name;
            product.ShortCode = dto.ShortCode;
            product.UnitValue = dto.UnitValue;
            product.UnitOfMeasure = dto.UnitOfMeasure;
            product.Barcode = dto.Barcode;
            product.Sku = dto.SKU;
            product.ImageUrl = dto.ImageUrl;
            product.Price = dto.Price;
            product.CostPrice = dto.CostPrice;
            product.TaxId = dto.TaxId;
            product.ProductType = dto.ProductType;
            product.PricingMethod = POS.Shared.Constants.PricingMethods.Normalize(dto.PricingMethod);
            product.IsActive = dto.IsActive;
            product.IsKitchenItem = dto.IsKitchenItem;
            product.IsBarItem = dto.IsBarItem;
            if (dto.DisplayOrder > 0)
            {
                product.DisplayOrder = dto.DisplayOrder;
            }

            await _context.SaveChangesAsync();
            return NoContent();
        }

        // PUT: api/Products/category/5/reorder
        [HttpPut("category/{categoryId}/reorder")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> ReorderProducts(int categoryId, [FromBody] List<int> orderedProductIds)
        {
            if (orderedProductIds == null || orderedProductIds.Count == 0)
            {
                return BadRequest("Product IDs list cannot be empty.");
            }

            var products = await _context.Products
                .Where(p => p.CategoryId == categoryId && !p.IsDeleted && orderedProductIds.Contains(p.ProductId))
                .ToListAsync();

            var productMap = products.ToDictionary(p => p.ProductId);
            for (int i = 0; i < orderedProductIds.Count; i++)
            {
                var id = orderedProductIds[i];
                if (productMap.TryGetValue(id, out var prod))
                {
                    prod.DisplayOrder = i + 1;
                }
            }

            await _context.SaveChangesAsync();
            return Ok();
        }

        // DELETE: api/Products/remove-product/5
        [HttpDelete("remove-product/{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> DeleteProduct(int id)
        {
            var product = await _context.Products.FindAsync(id);
            if (product == null) return NotFound();

            product.IsDeleted = true;
            product.IsActive = false;
            await _context.SaveChangesAsync();

            return NoContent();
        }


        // POST: api/Products/upload-image
        [HttpPost("upload-image")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UploadImage(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded.");

            if (file.Length > 5 * 1024 * 1024)
                return BadRequest("Image file size exceeds the 5MB limit.");

            // Validate extension
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
            var extension = Path.GetExtension(file.FileName).ToLowerInvariant();

            if (!allowedExtensions.Contains(extension))
                return BadRequest("Invalid image format. Allowed formats: JPG, JPEG, PNG, WEBP.");

            try
            {
                var webRoot = _env.WebRootPath;
                if (string.IsNullOrWhiteSpace(webRoot))
                {
                    webRoot = Path.Combine(_env.ContentRootPath ?? Directory.GetCurrentDirectory(), "wwwroot");
                }

                // Ensure wwwroot/uploads/products directory exists
                var uploadsFolder = Path.Combine(webRoot, "uploads", "products");
                if (!Directory.Exists(uploadsFolder))
                {
                    Directory.CreateDirectory(uploadsFolder);
                }

                // Generate unique filename to avoid overwrites
                var uniqueFileName = $"{Guid.NewGuid()}{extension}";
                var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await file.CopyToAsync(stream);
                }

                // Relative URL saved to database
                var relativeUrl = $"/uploads/products/{uniqueFileName}";
                return Ok(new { imageUrl = relativeUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to save image on server: {ex.Message}");
            }
        }
    }
}
