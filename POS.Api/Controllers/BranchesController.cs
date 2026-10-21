using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Admin,Manager")]
    public class BranchesController : ControllerBase
    {
        private readonly PosDbContext _context;

        public BranchesController(PosDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        public async Task<ActionResult<IEnumerable<BranchDto>>> GetBranches()
        {
            var entities = await _context.Branches.AsNoTracking()
                .Include(b => b.BusinessType).Include(b => b.ServiceMode)
                .Include(b => b.Outlets).ThenInclude(o => o.Terminals).ToListAsync();
            var branches = entities.Select(b => new BranchDto
            {
                BranchId = b.BranchId, BusinessTypeId = b.BusinessTypeId, BusinessTypeName = b.BusinessType?.Type,
                ServiceModeId = b.ServiceModeId, ServiceModeName = b.ServiceMode?.Type,
                TrackInventory = b.TrackInventory, Name = b.Name, Code = b.Code, Address = b.Address,
                Phone = b.Phone, TaxType = b.TaxType, IsActive = b.IsActive,
                OutletCount = b.Outlets.Count, TerminalCount = b.Outlets.Sum(o => o.Terminals.Count),
                Outlets = b.Outlets.OrderBy(o => o.OutletCode).Select(HierarchyMapping.ToDto).ToList(),
                Terminals = b.Outlets.SelectMany(o => o.Terminals).Select(HierarchyMapping.ToDto).ToList()
            }).ToList();
            return Ok(branches);
        }

        [HttpGet("{id:int}")]
        public async Task<ActionResult<BranchDto>> GetBranch(int id)
        {
            var response = await GetBranches();
            var branch = ((OkObjectResult)response.Result!).Value is List<BranchDto> all ? all.SingleOrDefault(b => b.BranchId == id) : null;
            return branch == null ? NotFound() : Ok(branch);
        }

        [HttpPost]
        public async Task<ActionResult<BranchDto>> CreateBranch(BranchDto dto)
        {
            if (dto.BusinessTypeId.HasValue && !await _context.BusinessTypes.AnyAsync(x => x.Id == dto.BusinessTypeId))
                return BadRequest("The selected business type does not exist.");
            var branch = new Branch
            {
                BusinessTypeId = dto.BusinessTypeId,
                ServiceModeId = dto.ServiceModeId,
                Name = dto.Name,
                Code = dto.Code,
                Address = dto.Address,
                Phone = dto.Phone,
                TaxType = dto.TaxType,
                IsActive = dto.IsActive,
                TrackInventory = dto.TrackInventory
            };

            _context.Branches.Add(branch);
            await _context.SaveChangesAsync();

            dto.BranchId = branch.BranchId;
            return Ok(dto);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateBranch(int id, BranchDto dto)
        {
            var branch = await _context.Branches.FindAsync(id);
            if (branch == null) return NotFound();

            if (dto.BusinessTypeId.HasValue && !await _context.BusinessTypes.AnyAsync(x => x.Id == dto.BusinessTypeId))
                return BadRequest("The selected business type does not exist.");
            if (!dto.IsActive && await _context.Outlets.AnyAsync(o => o.BranchId == id && o.IsActive))
                return Conflict("Deactivate outlets and terminals before deactivating the branch.");
            branch.BusinessTypeId = dto.BusinessTypeId;
            branch.ServiceModeId = dto.ServiceModeId;
            branch.Name = dto.Name;
            branch.Code = dto.Code;
            branch.Address = dto.Address;
            branch.Phone = dto.Phone;
            branch.TaxType = dto.TaxType;
            branch.IsActive = dto.IsActive;
            branch.TrackInventory = dto.TrackInventory;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteBranch(int id)
        {
            var branch = await _context.Branches.FindAsync(id);
            if (branch == null) return NotFound();

            if (await _context.Outlets.AnyAsync(o => o.BranchId == id)
                || await _context.Orders.AnyAsync(o => o.BranchId == id)
                || await _context.CashDrawerShifts.AnyAsync(o => o.BranchId == id))
                return Conflict("Branch has outlets or historical records. Deactivate it instead.");
            _context.Branches.Remove(branch);
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
