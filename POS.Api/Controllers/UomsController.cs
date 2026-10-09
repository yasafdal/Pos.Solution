using Microsoft.AspNetCore.Mvc;
using POS.Api.Models;
using POS.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UomsController : ControllerBase
    {
        private readonly PosDbContext _context;
        public UomsController(PosDbContext context)
        {
            _context = context;
        }

        // GET: api/Uoms
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UomDto>>> GetUoms()
        {
            var uoms = await _context.UnitOfMeasures
                .Where(u => !u.IsDeleted)
                .Select(u => new UomDto
                {
                    UomId = u.UomId,
                    Name = u.Name,
                    Abbreviation = u.Abbreviation,
                    IsActive = u.IsActive
                })
                .ToListAsync();

            return Ok(uoms);
        }

        // GET: api/Uoms/5
        [HttpGet("{id}")]
        public async Task<ActionResult<UomDto>> GetUom(int id)
        {
            var u = await _context.UnitOfMeasures.FindAsync(id);
            if (u == null || u.IsDeleted) return NotFound();

            return Ok(new UomDto
            {
                UomId = u.UomId,
                Name = u.Name,
                Abbreviation = u.Abbreviation,
                IsActive = u.IsActive
            });
        }

        // POST: api/Uoms
        [HttpPost]
        public async Task<ActionResult<UomDto>> CreateUom(UomDto dto)
        {
            var uom = new UnitOfMeasure
            {
                Name = dto.Name,
                Abbreviation = dto.Abbreviation,
                IsActive = dto.IsActive
            };

            _context.UnitOfMeasures.Add(uom);
            await _context.SaveChangesAsync();

            dto.UomId = uom.UomId;
            return CreatedAtAction(nameof(GetUom), new { id = uom.UomId }, dto);
        }

        // PUT: api/Uoms/5
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateUom(int id, UomDto dto)
        {
            var uom = await _context.UnitOfMeasures.FindAsync(id);
            if (uom == null || uom.IsDeleted) return NotFound();

            uom.Name = dto.Name;
            uom.Abbreviation = dto.Abbreviation;
            uom.IsActive = dto.IsActive;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        // DELETE: api/Uoms/5
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUom(int id)
        {
            var uom = await _context.UnitOfMeasures.FindAsync(id);
            if (uom == null || uom.IsDeleted) return NotFound();

            // Soft delete
            uom.IsDeleted = true;
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
