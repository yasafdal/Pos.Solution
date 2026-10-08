using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
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
            var branches = await _context.Branches
                .Include(b => b.Outlets)
                .Select(b => new BranchDto
                {
                    BranchId = b.BranchId,
                    Name = b.Name,
                    Code = b.Code,
                    Address = b.Address,
                    Phone = b.Phone,
                    TaxType = b.TaxType,
                    IsActive = b.IsActive,
                    OutletCount = b.Outlets.Count,
                    Outlets = b.Outlets.Select(o => new OutletDto
                    {
                        OutletId = o.OutletId,
                        BranchId = o.BranchId,
                        BranchName = b.Name,
                        Name = o.Name,
                        DeviceIdentifier = o.DeviceIdentifier,
                        PTUNumber = o.Ptunumber,
                        MINNumber = o.Minnumber,
                        SerialNumber = o.SerialNumber,
                        IsActive = o.IsActive
                    }).ToList()
                })
                .ToListAsync();

            return Ok(branches);
        }

        [HttpPost]
        public async Task<ActionResult<BranchDto>> CreateBranch(BranchDto dto)
        {
            var branch = new Branch
            {
                Name = dto.Name,
                Code = dto.Code,
                Address = dto.Address,
                Phone = dto.Phone,
                TaxType = dto.TaxType,
                IsActive = dto.IsActive
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

            branch.Name = dto.Name;
            branch.Code = dto.Code;
            branch.Address = dto.Address;
            branch.Phone = dto.Phone;
            branch.TaxType = dto.TaxType;
            branch.IsActive = dto.IsActive;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteBranch(int id)
        {
            var branch = await _context.Branches.FindAsync(id);
            if (branch == null) return NotFound();

            _context.Branches.Remove(branch);
            await _context.SaveChangesAsync();
            return NoContent();
        }
    }
}
