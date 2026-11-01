using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class TaxesController : ControllerBase
{
    private readonly PosDbContext _context;
    public TaxesController(PosDbContext context) => _context = context;

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TaxDto>>> GetTaxes()
    {
        return await _context.Taxes
            .Select(t => new TaxDto { TaxId = t.TaxId, Name = t.Name, Rate = t.Rate ?? 0m })
            .ToListAsync();
    }
}