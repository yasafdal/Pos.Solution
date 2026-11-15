using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using POS.Shared.Helpers;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class CategoriesController : ControllerBase
    {
        private readonly PosDbContext _context;

        public CategoriesController(PosDbContext context)
        {
            _context = context;
        }

        // GET: api/categories
        // Only returns categories that are active
        [HttpGet]
        public async Task<ActionResult<IEnumerable<CategoryDto>>> GetCategories()
        {
            var categories = await _context.Categories
                .Where(c => c.IsDeleted == false)
                .OrderBy(c => c.DisplayOrder)
                .ThenBy(c => c.CategoryId)
                .Select(c => new CategoryDto
                {
                    CategoryId = c.CategoryId,
                    Name = c.Name,
                    Description = c.Description,
                    ColorHex = CategoryColor.Normalize(c.ColorHex),
                    IsActive = c.IsActive,
                    DisplayOrder = c.DisplayOrder,
                    ProductCount = _context.Products.Count(p => p.CategoryId == c.CategoryId)
                })
                .ToListAsync();

            return Ok(categories);
        }

        // GET: api/categories/5
        [HttpGet("get-category/{id}")]
        public async Task<ActionResult<CategoryDto>> GetCategory(int id)
        {
            var category = await _context.Categories
                .Where(c => c.IsActive && !c.IsDeleted && c.CategoryId == id)
                .Select(c => new CategoryDto
                {
                    CategoryId = c.CategoryId,
                    Name = c.Name,
                    Description = c.Description,
                    ColorHex = CategoryColor.Normalize(c.ColorHex),
                    IsActive = c.IsActive,
                    DisplayOrder = c.DisplayOrder
                })
                .FirstOrDefaultAsync();

            if (category == null)
            {
                return NotFound();
            }

            return Ok(category);
        }

        // POST: api/categories
        [HttpPost("create-category")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<ActionResult<CategoryDto>> CreateCategory(CategoryDto categoryDto)
        {
            int displayOrder = categoryDto.DisplayOrder;
            if (displayOrder <= 0)
            {
                var maxOrder = await _context.Categories.MaxAsync(c => (int?)c.DisplayOrder) ?? 0;
                displayOrder = maxOrder + 1;
            }

            var category = new Category
            {
                Name = categoryDto.Name,
                Description = categoryDto.Description,
                ColorHex = categoryDto.ColorHex.ToUpperInvariant(),
                IsActive = categoryDto.IsActive,
                DisplayOrder = displayOrder
            };

            _context.Categories.Add(category);
            await _context.SaveChangesAsync();

            // Update the DTO with the database-generated ID
            categoryDto.CategoryId = category.CategoryId;
            categoryDto.ColorHex = category.ColorHex;
            categoryDto.DisplayOrder = category.DisplayOrder;

            return CreatedAtAction(nameof(GetCategory), new { id = category.CategoryId }, categoryDto);
        }

        // PUT: api/categories/5
        [HttpPut("update-category/{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UpdateCategory(int id, CategoryDto categoryDto)
        {
            if (id != categoryDto.CategoryId)
            {
                return BadRequest("ID mismatch");
            }

            var category = await _context.Categories.FindAsync(id);
            if (category == null || !category.IsActive || category.IsDeleted)
            {
                return NotFound();
            }

            // Update allowed fields
            category.Name = categoryDto.Name;
            category.Description = categoryDto.Description;
            category.ColorHex = categoryDto.ColorHex.ToUpperInvariant();
            category.IsActive = categoryDto.IsActive;
            if (categoryDto.DisplayOrder > 0)
            {
                category.DisplayOrder = categoryDto.DisplayOrder;
            }

            await _context.SaveChangesAsync();

            return NoContent();
        }

        // PUT: api/categories/reorder
        [HttpPut("reorder")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> ReorderCategories([FromBody] List<int> orderedCategoryIds)
        {
            if (orderedCategoryIds == null || orderedCategoryIds.Count == 0)
            {
                return BadRequest("Category IDs list cannot be empty.");
            }

            var categories = await _context.Categories
                .Where(c => !c.IsDeleted && orderedCategoryIds.Contains(c.CategoryId))
                .ToListAsync();

            var categoryMap = categories.ToDictionary(c => c.CategoryId);
            for (int i = 0; i < orderedCategoryIds.Count; i++)
            {
                var id = orderedCategoryIds[i];
                if (categoryMap.TryGetValue(id, out var cat))
                {
                    cat.DisplayOrder = i + 1;
                }
            }

            await _context.SaveChangesAsync();
            return Ok();
        }

        // DELETE: api/categories/5
        // Soft delete: Hides the category but keeps it in the DB for historical receipts
        [HttpDelete("remove-category/{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> DeleteCategory(int id)
        {
            var category = await _context.Categories.FindAsync(id);
            if (category == null || category.IsDeleted)
            {
                return NotFound();
            }

            category.IsDeleted = true;
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
