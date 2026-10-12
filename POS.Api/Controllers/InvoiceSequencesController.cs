using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/outlets/{outletId}/invoice-sequences")]
[ApiController]
[Authorize]
public class InvoiceSequencesController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<InvoiceSequence>>> GetInvoiceSequences(int outletId)
    {
        var list = await context.InvoiceSequences
            .AsNoTracking()
            .Where(s => s.OutletId == outletId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("active")]
    public async Task<ActionResult<InvoiceSequence>> GetActiveInvoiceSequence(int outletId)
    {
        var seq = await context.InvoiceSequences
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.OutletId == outletId && s.IsActive);
        if (seq == null) return NotFound("No active invoice sequence found for outlet.");
        return Ok(seq);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<InvoiceSequence>> CreateInvoiceSequence(int outletId, [FromBody] InvoiceSequenceDto dto)
    {
        var activeSeqs = await context.InvoiceSequences
            .Where(s => s.OutletId == outletId && s.IsActive)
            .ToListAsync();

        foreach (var s in activeSeqs)
        {
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
        }

        var seq = new InvoiceSequence
        {
            OutletId = outletId,
            SeriesCode = dto.SeriesCode,
            Prefix = dto.Prefix,
            StartingNumber = dto.StartingNumber,
            EndingNumber = dto.EndingNumber,
            LastIssuedNumber = dto.StartingNumber - 1,
            PaddingLength = dto.PaddingLength > 0 ? dto.PaddingLength : 7,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        context.InvoiceSequences.Add(seq);
        await context.SaveChangesAsync();
        return Ok(seq);
    }

    [HttpPost("{sequenceId}/activate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ActivateSequence(int outletId, int sequenceId)
    {
        var seq = await context.InvoiceSequences
            .SingleOrDefaultAsync(s => s.InvoiceSequenceId == sequenceId && s.OutletId == outletId);
        if (seq == null) return NotFound("Sequence not found.");

        var activeSeqs = await context.InvoiceSequences
            .Where(s => s.OutletId == outletId && s.IsActive)
            .ToListAsync();

        foreach (var s in activeSeqs)
        {
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
        }

        seq.IsActive = true;
        seq.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();

        return NoContent();
    }
}
