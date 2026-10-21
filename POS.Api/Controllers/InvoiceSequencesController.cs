using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/terminals/{terminalId}/invoice-sequences")]
[ApiController]
[Authorize]
public class InvoiceSequencesController(PosDbContext context) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<InvoiceSequence>>> GetInvoiceSequences(int terminalId)
    {
        var list = await context.InvoiceSequences
            .AsNoTracking()
            .Where(s => s.TerminalId == terminalId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("active")]
    public async Task<ActionResult<InvoiceSequence>> GetActiveInvoiceSequence(int terminalId)
    {
        var seq = await context.InvoiceSequences
            .AsNoTracking()
            .SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.IsActive);
        if (seq == null) return NotFound("No active invoice sequence found for outlet.");
        return Ok(seq);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<InvoiceSequence>> CreateInvoiceSequence(int terminalId, [FromBody] InvoiceSequenceDto dto)
    {
        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        if (string.IsNullOrWhiteSpace(dto.SeriesCode) || dto.SeriesCode.Length > 50 || dto.Prefix?.Length > 20
            || dto.StartingNumber < 1 || (dto.EndingNumber.HasValue && dto.EndingNumber < dto.StartingNumber)
            || dto.PaddingLength is < 1 or > 18)
            return BadRequest("A valid series code, positive range, and padding from 1 to 18 are required.");
        if (await context.InvoiceSequences.AnyAsync(s => (s.Prefix ?? "") == (dto.Prefix ?? "")
            && (!s.EndingNumber.HasValue || s.EndingNumber >= dto.StartingNumber)
            && (!dto.EndingNumber.HasValue || s.StartingNumber <= dto.EndingNumber)))
            return Conflict("This invoice prefix/range overlaps an existing allocation. Use a distinct terminal prefix or non-overlapping range.");
        if (!await context.Terminals.AnyAsync(t => t.TerminalId == terminalId && t.IsActive && t.Outlet.IsActive && t.Outlet.Branch.IsActive))
            return BadRequest("Select an active terminal, outlet and branch.");
        var activeSeqs = await context.InvoiceSequences
            .Where(s => s.TerminalId == terminalId && s.IsActive)
            .ToListAsync();

        foreach (var s in activeSeqs)
        {
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
        }

        var seq = new InvoiceSequence
        {
            TerminalId = terminalId,
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
        await transaction.CommitAsync();
        return Ok(seq);
    }

    [HttpPost("{sequenceId}/activate")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> ActivateSequence(int terminalId, int sequenceId)
    {
        var seq = await context.InvoiceSequences
            .SingleOrDefaultAsync(s => s.InvoiceSequenceId == sequenceId && s.TerminalId == terminalId);
        if (seq == null) return NotFound("Sequence not found.");

        if (!await context.Terminals.AnyAsync(t => t.TerminalId == terminalId && t.IsActive && t.Outlet.IsActive && t.Outlet.Branch.IsActive))
            return BadRequest("Select an active terminal, outlet and branch.");
        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var activeSeqs = await context.InvoiceSequences
            .Where(s => s.TerminalId == terminalId && s.IsActive)
            .ToListAsync();

        foreach (var s in activeSeqs)
        {
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
        }

        seq.IsActive = true;
        seq.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync();
        await transaction.CommitAsync();

        return NoContent();
    }
}
