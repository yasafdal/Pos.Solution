using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class ProductRecipesController : ControllerBase
    {
        private readonly PosDbContext _context;

        public ProductRecipesController(PosDbContext context)
        {
            _context = context;
        }

        /// GET: api/ProductRecipes/product/5
        [HttpGet("product/{productId}")]
        public async Task<ActionResult<IEnumerable<ProductRecipeDto>>> GetRecipeForProduct(int productId)
        {
            var recipe = await _context.ProductRecipes
                .Include(pr => pr.Product)
                .Include(pr => pr.RawMaterialProduct) // Updated to match EF generated property name
                .Where(pr => pr.ProductId == productId)
                .Select(pr => new ProductRecipeDto
                {
                    RecipeId = pr.RecipeId,
                    ProductId = pr.ProductId,
                    ProductName = pr.Product != null ? pr.Product.Name : "Unknown",
                    RawMaterialProductId = pr.RawMaterialProductId,
                    RawMaterialName = pr.RawMaterialProduct != null ? pr.RawMaterialProduct.Name : "Unknown",
                    QuantityRequired = pr.QuantityRequired,
                    Uom = pr.Uom
                })
                .ToListAsync();

            return Ok(recipe);
        }

        // POST: api/ProductRecipes
        [HttpPost]
        public async Task<ActionResult<ProductRecipeDto>> AddIngredientToRecipe(ProductRecipeDto dto)
        {
            var recipeItem = new ProductRecipe
            {
                ProductId = dto.ProductId,
                RawMaterialProductId = dto.RawMaterialProductId,
                QuantityRequired = dto.QuantityRequired,
                Uom = dto.Uom
            };

            _context.ProductRecipes.Add(recipeItem);

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                return BadRequest("This ingredient is already in the recipe.");
            }

            dto.RecipeId = recipeItem.RecipeId;
            return Ok(dto);
        }

        // PUT: api/ProductRecipes/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateIngredientQuantity(int id, ProductRecipeDto dto)
        {
            var recipeItem = await _context.ProductRecipes.FindAsync(id);
            if (recipeItem == null) return NotFound();

            recipeItem.QuantityRequired = dto.QuantityRequired;
            recipeItem.Uom = dto.Uom;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/ProductRecipes/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> RemoveIngredientFromRecipe(int id)
        {
            var recipeItem = await _context.ProductRecipes.FindAsync(id);
            if (recipeItem == null) return NotFound();

            _context.ProductRecipes.Remove(recipeItem);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
