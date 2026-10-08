using Microsoft.AspNetCore.Mvc;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OutletsController : ControllerBase
    {
        private readonly PosDbContext _context;

        public OutletsController(PosDbContext context)
        {
            _context = context;
        }

        [HttpPost]
        public async Task<ActionResult<OutletDto>> CreateOutlet(OutletDto dto)
        {
            var outlet = new Outlet
            {
                BranchId = dto.BranchId,
                Name = dto.Name,
                DeviceIdentifier = dto.DeviceIdentifier,
                Ptunumber = dto.PTUNumber,
                Minnumber = dto.MINNumber,
                SerialNumber = dto.SerialNumber,
                IsActive = dto.IsActive
            };

            _context.Outlets.Add(outlet);
            await _context.SaveChangesAsync();

            dto.OutletId = outlet.OutletId;
            return Ok(dto);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateOutlet(int id, OutletDto dto)
        {
            var outlet = await _context.Outlets.FindAsync(id);
            if (outlet == null) return NotFound();

            outlet.Name = dto.Name;
            outlet.DeviceIdentifier = dto.DeviceIdentifier;
            outlet.Ptunumber = dto.PTUNumber;
            outlet.Minnumber = dto.MINNumber;
            outlet.SerialNumber = dto.SerialNumber;
            outlet.IsActive = dto.IsActive;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteOutlet(int id)
        {
            var outlet = await _context.Outlets.FindAsync(id);
            if (outlet == null) return NotFound();

            _context.Outlets.Remove(outlet);
            await _context.SaveChangesAsync();
            return NoContent();
        }

    }
}
