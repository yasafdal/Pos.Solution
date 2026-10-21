using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/outlets")]
[ApiController]
[Authorize(Roles = "Admin,Manager")]
public class OutletsController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<OutletDto>>> GetOutlets([FromQuery] int? branchId)
    {
        var query = context.Outlets.AsNoTracking().Include(o => o.Terminals).AsQueryable();
        if (branchId.HasValue) query = query.Where(o => o.BranchId == branchId);
        return Ok((await query.OrderBy(o => o.OutletCode).ToListAsync()).Select(HierarchyMapping.ToDto).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OutletDto>> GetOutlet(int id)
    {
        var outlet = await context.Outlets.AsNoTracking().Include(o => o.Terminals).SingleOrDefaultAsync(o => o.OutletId == id);
        return outlet == null ? NotFound() : Ok(HierarchyMapping.ToDto(outlet));
    }

    [HttpPost]
    public async Task<ActionResult<OutletDto>> CreateOutlet(OutletDto dto)
    {
        var error = await Validate(dto, 0);
        if (error != null) return BadRequest(error);
        var outlet = new Outlet { BranchId = dto.BranchId, CreatedAt = DateTime.UtcNow };
        Apply(outlet, dto);
        context.Outlets.Add(outlet);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict("The outlet code already exists or branch ownership changed. Refresh and retry."); }
        return CreatedAtAction(nameof(GetOutlet), new { id = outlet.OutletId }, HierarchyMapping.ToDto(outlet));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateOutlet(int id, OutletDto dto)
    {
        var outlet = await context.Outlets.FindAsync(id);
        if (outlet == null) return NotFound();
        if (dto.BranchId != outlet.BranchId) return BadRequest("An outlet cannot be moved to another branch.");
        var error = await Validate(dto, id);
        if (error != null) return BadRequest(error);
        if (!dto.IsActive && await context.Terminals.AnyAsync(t => t.OutletId == id && t.IsActive))
            return Conflict("Deactivate assigned terminals before deactivating the outlet.");
        Apply(outlet, dto);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict("The outlet code is already in use. Refresh and retry."); }
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteOutlet(int id)
    {
        var outlet = await context.Outlets.FindAsync(id);
        if (outlet == null) return NotFound();
        if (await context.Terminals.AnyAsync(t => t.OutletId == id))
            return Conflict("This outlet has terminals. Deactivate it instead of deleting history.");
        context.Outlets.Remove(outlet);
        try { await context.SaveChangesAsync(); }
        catch (DbUpdateException) { return Conflict("The outlet has related records and cannot be deleted."); }
        return NoContent();
    }

    private async Task<string?> Validate(OutletDto dto, int id)
    {
        dto.OutletCode = dto.OutletCode.Trim().ToUpperInvariant();
        dto.Name = dto.Name.Trim();
        if (dto.OutletCode.Length is < 1 or > 30 || dto.Name.Length is < 1 or > 100)
            return "Outlet code and name are required and must fit their maximum lengths.";
        if (!new[] { "Main", "Booth", "Kiosk", "Counter", "Mobile" }.Contains(dto.OutletType)) return "Invalid outlet type.";
        var branch = await context.Branches.AsNoTracking().SingleOrDefaultAsync(b => b.BranchId == dto.BranchId);
        if (branch == null || (dto.IsActive && !branch.IsActive)) return "Select an existing active branch.";
        if (await context.Outlets.AnyAsync(o => o.BranchId == dto.BranchId && o.OutletId != id && o.OutletCode.ToUpper() == dto.OutletCode))
            return "Outlet code must be unique within its branch.";
        return null;
    }

    private static void Apply(Outlet o, OutletDto dto)
    {
        o.OutletCode = dto.OutletCode; o.Name = dto.Name; o.Description = dto.Description;
        o.LocationDescription = dto.LocationDescription; o.OutletType = dto.OutletType;
        if (o.IsActive && !dto.IsActive)
        {
            o.DeactivatedAt ??= DateTime.UtcNow;
        }
        else if (!o.IsActive && dto.IsActive)
        {
            o.DeactivatedAt = null;
        }
        o.IsActive = dto.IsActive; o.UpdatedAt = DateTime.UtcNow;
    }
}
