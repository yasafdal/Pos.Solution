using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class BranchesController : ControllerBase
    {
        private readonly PosDbContext _context;

        public BranchesController(PosDbContext context)
        {
            _context = context;
        }

        [HttpGet]
        [Authorize(Roles = "Admin,Manager,AgresSystemAdmin")]
        public async Task<ActionResult<IEnumerable<BranchDto>>> GetBranches()
        {
            await POS.Api.Services.ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
            bool isAgresAdmin = POS.Shared.Constants.PosPermissions.IsAgresSystemAdmin(User);
            var query = _context.Branches.AsNoTracking()
                .Include(b => b.BusinessType).Include(b => b.ServiceMode)
                .Include(b => b.Outlets).ThenInclude(o => o.Terminals).AsQueryable();

            if (!isAgresAdmin)
            {
                var callerOrgId = POS.Shared.Constants.PosPermissions.GetOrganizationId(User);
                if (callerOrgId.HasValue)
                    query = query.Where(b => b.OrganizationId == callerOrgId.Value);

                if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId))
                {
                    query = query.Where(b => b.BranchId == supBranchId);
                }
            }

            var entities = await query.ToListAsync();
            var branches = entities.Select(b => new BranchDto
            {
                BranchId = b.BranchId, BusinessTypeId = b.BusinessTypeId, BusinessTypeName = b.BusinessType?.Type,
                ServiceModeId = b.ServiceModeId, ServiceModeName = b.ServiceMode?.Type,
                TrackInventory = b.TrackInventory, Name = b.Name, BranchTagline = b.BranchTagline, Code = b.Code, Address = b.Address,
                Phone = b.Phone, TaxType = b.TaxType, VatDisplay = b.VatDisplay, IsActive = b.IsActive,
                ReceiptLogoUrl = b.ReceiptLogoUrl,
                ReceiptLogoDataUri = POS.Api.Services.ReceiptLogoStorage.GetDataUri(b.ReceiptLogoUrl),
                ReceiptLogoUpdatedAt = b.ReceiptLogoUpdatedAt,
                OutletCount = b.Outlets.Count, TerminalCount = b.Outlets.Sum(o => o.Terminals.Count),
                Outlets = b.Outlets.OrderBy(o => o.OutletCode).Select(HierarchyMapping.ToDto).ToList(),
                Terminals = b.Outlets.SelectMany(o => o.Terminals).Select(t =>
                {
                    var dto = HierarchyMapping.ToDto(t);
                    if (!isAgresAdmin) dto.ActivationCode = null;
                    return dto;
                }).ToList()
            }).ToList();
            return Ok(branches);
        }

        [HttpGet("{id:int}")]
        [Authorize(Roles = "Admin,Manager,AgresSystemAdmin")]
        public async Task<ActionResult<BranchDto>> GetBranch(int id)
        {
            if (!POS.Shared.Constants.PosPermissions.IsAgresSystemAdmin(User) &&
                !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && id != supBranchId)
                return Forbid();

            var response = await GetBranches();
            var branch = ((OkObjectResult)response.Result!).Value is List<BranchDto> all ? all.SingleOrDefault(b => b.BranchId == id) : null;
            return branch == null ? NotFound() : Ok(branch);
        }

        [HttpGet("{id:int}/client-config")]
        [Authorize]
        public async Task<ActionResult<OfflineBranchConfigDto>> GetBranchClientConfig(int id)
        {
            await POS.Api.Services.ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
            if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var tokenBranchId) && tokenBranchId > 0 && tokenBranchId != id)
                return Forbid();

            var b = await _context.Branches.AsNoTracking()
                .Include(x => x.BusinessType)
                .SingleOrDefaultAsync(x => x.BranchId == id);
            if (b == null) return NotFound();

            return Ok(new OfflineBranchConfigDto
            {
                BranchId = b.BranchId,
                Name = b.Name,
                BranchTagline = b.BranchTagline,
                Code = b.Code ?? $"BR-{b.BranchId:D2}",
                Address = b.Address,
                Phone = b.Phone,
                TaxType = b.TaxType ?? "VAT-Registered",
                VatDisplay = b.VatDisplay,
                TrackInventory = b.TrackInventory,
                BusinessTypeId = b.BusinessTypeId,
                BusinessType = b.BusinessType?.Type,
                ServiceModeId = b.ServiceModeId,
                ReceiptLogoUrl = b.ReceiptLogoUrl,
                ReceiptLogoDataUri = POS.Api.Services.ReceiptLogoStorage.GetDataUri(b.ReceiptLogoUrl),
                ReceiptLogoUpdatedAt = b.ReceiptLogoUpdatedAt,
                IsActive = b.IsActive,
                UpdatedAt = DateTime.UtcNow
            });
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<ActionResult<BranchDto>> CreateBranch(BranchDto dto)
        {
            await POS.Api.Services.ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
            if (dto.BusinessTypeId.HasValue && !await _context.BusinessTypes.AnyAsync(x => x.Id == dto.BusinessTypeId))
                return BadRequest("The selected business type does not exist.");
            var normalizedTagline = string.IsNullOrWhiteSpace(dto.BranchTagline) ? null : dto.BranchTagline.Trim();
            if (normalizedTagline != null && normalizedTagline.Length > 200)
                return BadRequest("Branch tagline cannot exceed 200 characters.");
            var branch = new Branch
            {
                BusinessTypeId = dto.BusinessTypeId,
                ServiceModeId = dto.ServiceModeId,
                Name = dto.Name,
                BranchTagline = normalizedTagline,
                Code = dto.Code,
                Address = dto.Address,
                Phone = dto.Phone,
                TaxType = dto.TaxType,
                VatDisplay = dto.VatDisplay,
                IsActive = dto.IsActive,
                TrackInventory = dto.TrackInventory
            };

            _context.Branches.Add(branch);
            await _context.SaveChangesAsync();

            dto.BranchId = branch.BranchId;
            dto.BranchTagline = normalizedTagline;
            return Ok(dto);
        }

        [HttpPut("{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> UpdateBranch(int id, BranchDto dto)
        {
            await POS.Api.Services.ReceiptLogoStorage.EnsureBranchColumnsAsync(_context);
            if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && (id != supBranchId || dto.BranchId != supBranchId))
                return Forbid();

            var branch = await _context.Branches.FindAsync(id);
            if (branch == null) return NotFound();

            if (dto.BusinessTypeId.HasValue && !await _context.BusinessTypes.AnyAsync(x => x.Id == dto.BusinessTypeId))
                return BadRequest("The selected business type does not exist.");
            if (!dto.IsActive && await _context.Outlets.AnyAsync(o => o.BranchId == id && o.IsActive))
                return Conflict("Deactivate outlets and terminals before deactivating the branch.");
            var normalizedTagline = string.IsNullOrWhiteSpace(dto.BranchTagline) ? null : dto.BranchTagline.Trim();
            if (normalizedTagline != null && normalizedTagline.Length > 200)
                return BadRequest("Branch tagline cannot exceed 200 characters.");
            branch.BusinessTypeId = dto.BusinessTypeId;
            branch.ServiceModeId = dto.ServiceModeId;
            branch.Name = dto.Name;
            branch.BranchTagline = normalizedTagline;
            branch.Code = dto.Code;
            branch.Address = dto.Address;
            branch.Phone = dto.Phone;
            branch.TaxType = dto.TaxType;
            branch.VatDisplay = dto.VatDisplay;
            branch.IsActive = dto.IsActive;
            branch.TrackInventory = dto.TrackInventory;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin")]
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
