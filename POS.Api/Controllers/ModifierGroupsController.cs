using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ModifierGroupsController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ModifierGroupDto>>> GetModifierGroups() =>
        Ok(await context.ModifierGroups
            .Include(mg => mg.ModifierOptions.OrderBy(o => o.DisplayOrder))
            .AsNoTracking()
            .OrderBy(mg => mg.DisplayOrder)
            .ThenBy(mg => mg.Name)
            .Select(mg => ToDto(mg))
            .ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ModifierGroupDto>> GetModifierGroup(int id)
    {
        var mg = await context.ModifierGroups
            .Include(mg => mg.ModifierOptions.OrderBy(o => o.DisplayOrder))
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ModifierGroupId == id);

        return mg == null ? NotFound() : Ok(ToDto(mg));
    }

    [HttpGet("{groupId:int}/options")]
    public async Task<ActionResult<IEnumerable<ModifierOptionDto>>> GetOptionsByGroup(int groupId)
    {
        if (!await context.ModifierGroups.AnyAsync(x => x.ModifierGroupId == groupId))
            return NotFound("Modifier group not found.");

        var options = await context.ModifierOptions
            .Where(o => o.ModifierGroupId == groupId)
            .OrderBy(o => o.DisplayOrder)
            .ThenBy(o => o.Name)
            .Select(o => ToOptionDto(o))
            .ToListAsync();

        return Ok(options);
    }

    [HttpPost]
    public async Task<ActionResult<ModifierGroupDto>> CreateModifierGroup(ModifierGroupDto dto)
    {
        var mg = new ModifierGroup
        {
            Name = dto.Name.Trim(),
            IsRequired = dto.IsRequired,
            MinSelections = dto.MinSelections,
            MaxSelections = dto.MaxSelections,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        context.ModifierGroups.Add(mg);
        await context.SaveChangesAsync();

        dto.ModifierGroupId = mg.ModifierGroupId;
        return CreatedAtAction(nameof(GetModifierGroup), new { id = mg.ModifierGroupId }, dto);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateModifierGroup(int id, ModifierGroupDto dto)
    {
        var mg = await context.ModifierGroups.FindAsync(id);
        if (mg == null) return NotFound();

        mg.Name = dto.Name.Trim();
        mg.IsRequired = dto.IsRequired;
        mg.MinSelections = dto.MinSelections;
        mg.MaxSelections = dto.MaxSelections;
        mg.DisplayOrder = dto.DisplayOrder;
        mg.IsActive = dto.IsActive;

        await context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteModifierGroup(int id)
    {
        var mg = await context.ModifierGroups.FindAsync(id);
        if (mg == null) return NotFound();

        context.ModifierGroups.Remove(mg);
        await context.SaveChangesAsync();
        return NoContent();
    }

    private static ModifierGroupDto ToDto(ModifierGroup mg) => new()
    {
        ModifierGroupId = mg.ModifierGroupId,
        Name = mg.Name,
        IsRequired = mg.IsRequired,
        MinSelections = mg.MinSelections,
        MaxSelections = mg.MaxSelections,
        DisplayOrder = mg.DisplayOrder,
        IsActive = mg.IsActive,
        Options = mg.ModifierOptions?.Select(ToOptionDto).ToList() ?? new()
    };

    private static ModifierOptionDto ToOptionDto(ModifierOption o) => new()
    {
        ModifierOptionId = o.ModifierOptionId,
        ModifierGroupId = o.ModifierGroupId,
        Name = o.Name,
        Price = o.Price,
        DisplayOrder = o.DisplayOrder,
        IsActive = o.IsActive
    };
}
