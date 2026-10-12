using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModifierOptionsController(PosDbContext context) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ModifierOptionDto>> CreateOption(ModifierOptionDto dto)
    {
        if (!await context.ModifierGroups.AnyAsync(x => x.ModifierGroupId == dto.ModifierGroupId))
            return BadRequest("Modifier group does not exist.");

        var option = new ModifierOption
        {
            ModifierGroupId = dto.ModifierGroupId,
            Name = dto.Name.Trim(),
            Price = dto.Price,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        context.ModifierOptions.Add(option);
        await context.SaveChangesAsync();

        dto.ModifierOptionId = option.ModifierOptionId;
        return Ok(dto);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateOption(int id, ModifierOptionDto dto)
    {
        var option = await context.ModifierOptions.FindAsync(id);
        if (option == null) return NotFound();

        option.Name = dto.Name.Trim();
        option.Price = dto.Price;
        option.DisplayOrder = dto.DisplayOrder;
        option.IsActive = dto.IsActive;

        await context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteOption(int id)
    {
        var option = await context.ModifierOptions.FindAsync(id);
        if (option == null) return NotFound();

        context.ModifierOptions.Remove(option);
        await context.SaveChangesAsync();
        return NoContent();
    }
}
