using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class BusinessTypesController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<BusinessTypeDto>>> GetBusinessTypes() =>
        Ok(await context.BusinessTypes.AsNoTracking().OrderBy(x => x.Type)
            .Select(x => new BusinessTypeDto { Id = x.Id, Type = x.Type, Description = x.Description }).ToListAsync());

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateBusinessType(int id, BusinessTypeDto dto)
    {
        var item = await context.BusinessTypes.FindAsync(id);
        if (item == null) return NotFound();
        var type = dto.Type.Trim().ToUpperInvariant();
        if (await context.BusinessTypes.AnyAsync(x => x.Id != id && x.Type == type))
            return Conflict("A business type with this type already exists.");
        item.Type = type;
        item.Description = dto.Description.Trim();
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { return Conflict("A business type with this type already exists."); }
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteBusinessType(int id)
    {
        var item = await context.BusinessTypes.FindAsync(id);
        if (item == null) return NotFound();
        if (await context.Branches.AnyAsync(x => x.BusinessTypeId == id))
            return Conflict("This business type is assigned to a branch and cannot be deleted.");
        context.BusinessTypes.Remove(item);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" or "23001" })
        { return Conflict("This business type is assigned to a branch and cannot be deleted."); }
        return NoContent();
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<BusinessTypeDto>> GetBusinessType(int id)
    {
        var item = await context.BusinessTypes.FindAsync(id);
        return item == null ? NotFound() : Ok(ToDto(item));
    }

    [HttpPost]
    public async Task<ActionResult<BusinessTypeDto>> CreateBusinessType(BusinessTypeDto dto)
    {
        var item = new BusinessType { Type = dto.Type.Trim().ToUpperInvariant(), Description = dto.Description.Trim() };
        if (await context.BusinessTypes.AnyAsync(x => x.Type == item.Type))
            return Conflict("A business type with this type already exists.");
        context.BusinessTypes.Add(item);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { return Conflict("A business type with this type already exists."); }
        return CreatedAtAction(nameof(GetBusinessType), new { id = item.Id }, ToDto(item));
    }

    private static BusinessTypeDto ToDto(BusinessType item) => new() { Id = item.Id, Type = item.Type, Description = item.Description };
}
