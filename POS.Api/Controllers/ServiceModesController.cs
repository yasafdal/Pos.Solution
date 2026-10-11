using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ServiceModesController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ServiceModeDto>>> GetServiceModes() =>
        Ok(await context.ServiceModes.AsNoTracking().OrderBy(x => x.Type)
            .Select(x => new ServiceModeDto { ServiceModeId = x.ServiceModeId, Type = x.Type, Description = x.Description }).ToListAsync());

    [HttpGet("{id:int}")]
    public async Task<ActionResult<ServiceModeDto>> GetServiceMode(int id)
    {
        var item = await context.ServiceModes.FindAsync(id);
        return item == null ? NotFound() : Ok(ToDto(item));
    }

    [HttpPost]
    public async Task<ActionResult<ServiceModeDto>> CreateServiceMode(ServiceModeDto dto)
    {
        var item = new ServiceMode { Type = dto.Type.Trim().ToUpperInvariant(), Description = dto.Description.Trim() };
        if (await context.ServiceModes.AnyAsync(x => x.Type == item.Type))
            return Conflict("A service mode with this type already exists.");
        context.ServiceModes.Add(item);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { return Conflict("A service mode with this type already exists."); }
        return CreatedAtAction(nameof(GetServiceMode), new { id = item.ServiceModeId }, ToDto(item));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateServiceMode(int id, ServiceModeDto dto)
    {
        var item = await context.ServiceModes.FindAsync(id);
        if (item == null) return NotFound();
        var type = dto.Type.Trim().ToUpperInvariant();
        if (await context.ServiceModes.AnyAsync(x => x.ServiceModeId != id && x.Type == type))
            return Conflict("A service mode with this type already exists.");
        item.Type = type;
        item.Description = dto.Description.Trim();
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        { return Conflict("A service mode with this type already exists."); }
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteServiceMode(int id)
    {
        var item = await context.ServiceModes.FindAsync(id);
        if (item == null) return NotFound();
        if (await context.Branches.AnyAsync(x => x.ServiceModeId == id))
            return Conflict("This service mode is assigned to a branch and cannot be deleted.");
        context.ServiceModes.Remove(item);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23503" or "23001" })
        { return Conflict("This service mode is assigned to a branch and cannot be deleted."); }
        return NoContent();
    }

    private static ServiceModeDto ToDto(ServiceMode item) => new() { ServiceModeId = item.ServiceModeId, Type = item.Type, Description = item.Description };
}
