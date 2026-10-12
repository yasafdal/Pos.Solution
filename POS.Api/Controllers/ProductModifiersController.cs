using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/Products")]
public class ProductModifiersController(PosDbContext context) : ControllerBase
{
    [HttpGet("{productId:int}/modifiers")]
    public async Task<ActionResult<IEnumerable<ProductModifierGroupDto>>> GetProductModifiers(int productId)
    {
        if (!await context.Products.AnyAsync(p => p.ProductId == productId))
            return NotFound("Product not found.");

        var pmgList = await context.ProductModifierGroups
            .Include(pmg => pmg.ModifierGroup)
                .ThenInclude(mg => mg.ModifierOptions.OrderBy(o => o.DisplayOrder))
            .Where(pmg => pmg.ProductId == productId)
            .OrderBy(pmg => pmg.DisplayOrder)
            .Select(pmg => new ProductModifierGroupDto
            {
                ProductModifierGroupId = pmg.ProductModifierGroupId,
                ProductId = pmg.ProductId,
                ModifierGroupId = pmg.ModifierGroupId,
                ModifierGroupName = pmg.ModifierGroup != null ? pmg.ModifierGroup.Name : null,
                IsRequired = pmg.ModifierGroup != null && pmg.ModifierGroup.IsRequired,
                MinSelections = pmg.ModifierGroup != null ? pmg.ModifierGroup.MinSelections : 0,
                MaxSelections = pmg.ModifierGroup != null ? pmg.ModifierGroup.MaxSelections : 1,
                DisplayOrder = pmg.DisplayOrder,
                Options = pmg.ModifierGroup != null && pmg.ModifierGroup.ModifierOptions != null
                    ? pmg.ModifierGroup.ModifierOptions.Select(o => new ModifierOptionDto
                    {
                        ModifierOptionId = o.ModifierOptionId,
                        ModifierGroupId = o.ModifierGroupId,
                        Name = o.Name,
                        Price = o.Price,
                        DisplayOrder = o.DisplayOrder,
                        IsActive = o.IsActive
                    }).ToList()
                    : new()
            })
            .ToListAsync();

        return Ok(pmgList);
    }

    public class AssignModifierDto
    {
        public int ModifierGroupId { get; set; }
        public int DisplayOrder { get; set; }
    }

    [HttpPost("{productId:int}/modifiers")]
    public async Task<IActionResult> AssignModifierGroup(int productId, [FromBody] AssignModifierDto dto)
    {
        if (!await context.Products.AnyAsync(p => p.ProductId == productId))
            return NotFound("Product not found.");
        if (!await context.ModifierGroups.AnyAsync(mg => mg.ModifierGroupId == dto.ModifierGroupId))
            return NotFound("Modifier group not found.");

        if (await context.ProductModifierGroups.AnyAsync(pmg => pmg.ProductId == productId && pmg.ModifierGroupId == dto.ModifierGroupId))
            return Conflict("Modifier group already assigned to product.");

        var pmg = new ProductModifierGroup
        {
            ProductId = productId,
            ModifierGroupId = dto.ModifierGroupId,
            DisplayOrder = dto.DisplayOrder
        };

        context.ProductModifierGroups.Add(pmg);
        await context.SaveChangesAsync();

        return Ok();
    }

    [HttpDelete("{productId:int}/modifiers/{modifierGroupId:int}")]
    public async Task<IActionResult> RemoveModifierGroup(int productId, int modifierGroupId)
    {
        var pmg = await context.ProductModifierGroups
            .FirstOrDefaultAsync(x => x.ProductId == productId && x.ModifierGroupId == modifierGroupId);

        if (pmg == null) return NotFound();

        context.ProductModifierGroups.Remove(pmg);
        await context.SaveChangesAsync();

        return NoContent();
    }
}
