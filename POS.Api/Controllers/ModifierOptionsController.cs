using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ModifierOptionsController(PosDbContext context) : ControllerBase
{
    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<ModifierOptionDto>> CreateOption(ModifierOptionDto dto)
    {
        if (!await context.ModifierGroups.AnyAsync(x => x.ModifierGroupId == dto.ModifierGroupId))
            return BadRequest("Modifier group does not exist.");

        int? linkedProductId = dto.ProductId.HasValue && dto.ProductId.Value > 0 ? dto.ProductId.Value : null;
        string? linkedProductName = null;
        if (linkedProductId.HasValue)
        {
            var product = await context.Products.FirstOrDefaultAsync(p => p.ProductId == linkedProductId.Value && !p.IsDeleted);
            if (product == null)
                return BadRequest("Linked product does not exist or has been deleted.");
            if (!product.IsActive)
                return BadRequest("Linked product is currently inactive.");
            linkedProductName = product.Name;
        }

        var option = new ModifierOption
        {
            ModifierGroupId = dto.ModifierGroupId,
            ProductId = linkedProductId,
            Name = dto.Name.Trim(),
            Price = dto.Price,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        context.ModifierOptions.Add(option);
        await context.SaveChangesAsync();

        dto.ModifierOptionId = option.ModifierOptionId;
        dto.ProductId = linkedProductId;
        dto.ProductName = linkedProductName;
        return Ok(dto);
    }

    [HttpPut("{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateOption(int id, ModifierOptionDto dto)
    {
        var option = await context.ModifierOptions.FindAsync(id);
        if (option == null) return NotFound();

        int? linkedProductId = dto.ProductId.HasValue && dto.ProductId.Value > 0 ? dto.ProductId.Value : null;
        if (linkedProductId.HasValue)
        {
            var product = await context.Products.FirstOrDefaultAsync(p => p.ProductId == linkedProductId.Value && !p.IsDeleted);
            if (product == null)
                return BadRequest("Linked product does not exist or has been deleted.");
            if (!product.IsActive)
                return BadRequest("Linked product is currently inactive.");
        }

        option.ProductId = linkedProductId;
        option.Name = dto.Name.Trim();
        option.Price = dto.Price;
        option.DisplayOrder = dto.DisplayOrder;
        option.IsActive = dto.IsActive;

        await context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeleteOption(int id)
    {
        var option = await context.ModifierOptions.FindAsync(id);
        if (option == null) return NotFound();

        context.ModifierOptions.Remove(option);
        await context.SaveChangesAsync();
        return NoContent();
    }
}
