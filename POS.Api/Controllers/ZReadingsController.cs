using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[ApiController]
[Route("api/z-readings")]
[Authorize(Roles = "Admin,Manager,Senior Cashier,SeniorCashier")]
public sealed class ZReadingsController(PosDbContext db, IConfiguration configuration) : ControllerBase
{
    // Constructed from existing registered dependencies; no startup/DI or automatic schema changes.
    private ZReadingService Service => new(db, configuration);
    private bool Supervisor => User.Identity?.IsAuthenticated == true && (User.IsInRole("Admin") || User.IsInRole("Manager") || User.IsInRole("Senior Cashier") || User.IsInRole("SeniorCashier"));
    private string Operator => User.Identity?.Name ?? "";

    private async Task<bool> Allowed(int terminalId, bool currentTerminal, CancellationToken ct)
    {
        if (!Supervisor || string.IsNullOrWhiteSpace(Operator)) return false;
        if ((currentTerminal || (!User.IsInRole("Admin") && !User.IsInRole("Manager"))) &&
            (!int.TryParse(User.FindFirst("terminal_id")?.Value, out var t) || t != terminalId)) return false;
        var terminal = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(x => x.TerminalId == terminalId, ct);
        if (terminal == null) return false;
        if (!User.IsInRole("Admin") && (!int.TryParse(User.FindFirst("branch_id")?.Value, out var b) || b != terminal.BranchId)) return false;
        return true;
    }

    [HttpGet("terminals")]
    public async Task<IActionResult> Terminals(CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        var q = db.Terminals.AsNoTracking().Where(x => x.IsActive);
        if (!User.IsInRole("Admin"))
        {
            if (!int.TryParse(User.FindFirst("branch_id")?.Value, out var b)) return Forbid();
            q = q.Where(x => x.BranchId == b);
        }
        if (!User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            if (!int.TryParse(User.FindFirst("terminal_id")?.Value, out var t)) return Forbid();
            q = q.Where(x => x.TerminalId == t);
        }
        return Ok(await q.OrderBy(x => x.TerminalCode).Select(x => new ZReadingTerminalDto { TerminalId = x.TerminalId, BranchId = x.BranchId, OutletId = x.OutletId, TerminalCode = x.TerminalCode, Name = x.Name, DeviceIdentifier = x.DeviceIdentifier }).ToListAsync(ct));
    }

    [HttpGet("readiness")]
    public async Task<IActionResult> Readiness([FromQuery] int terminalId, [FromQuery] DateOnly businessDate, CancellationToken ct = default)
    {
        if (!await Allowed(terminalId, false, ct)) return Forbid();
        return Ok(await Service.ReadinessAsync(terminalId, businessDate, ct));
    }

    [HttpGet("by-date")]
    public async Task<IActionResult> ByDate([FromQuery] int terminalId, [FromQuery] DateOnly businessDate, CancellationToken ct = default)
    {
        if (!await Allowed(terminalId, false, ct)) return Forbid();
        try { var report = await Service.GetAsync(terminalId, businessDate, ct); return report == null ? NotFound() : Ok(report); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History(
        [FromQuery] int? branchId,
        [FromQuery] int? terminalId,
        [FromQuery] DateOnly? fromDate,
        [FromQuery] DateOnly? toDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        if (!Service.Enabled) return Conflict("Z-Reading is disabled.");

        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId))
        {
            if (branchId.HasValue && branchId.Value != supBranchId) return Forbid();
            branchId = supBranchId;
        }

        if (!User.IsInRole("Admin") && !User.IsInRole("Manager") && int.TryParse(User.FindFirst("terminal_id")?.Value, out var claimTerminalId))
        {
            if (terminalId.HasValue && terminalId.Value != claimTerminalId) return Forbid();
            terminalId = claimTerminalId;
        }

        try { return Ok(await Service.HistoryAsync(branchId, terminalId, fromDate, toDate, page, pageSize, ct)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        if (!Service.Enabled) return Conflict("Z-Reading is disabled.");

        var row = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row == null) return NotFound();

        if (!await Allowed(row.TerminalId, false, ct)) return Forbid();

        try { return Ok(ZReadingService.ReadSnapshot(row)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateZReadingRequest request, CancellationToken ct = default)
    {
        if (!await Allowed(request.TerminalId, true, ct)) return Forbid();
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(x => x.TerminalId == request.TerminalId, ct);
        if (terminal.DeviceIdentifier != null && terminal.DeviceIdentifier != request.DeviceIdentifier) return Forbid();
        try { return Ok(await Service.GenerateAsync(request, Operator, ct)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
        catch (DbUpdateException) { return Conflict("Concurrent generation or operation-ID collision. Retrieve by terminal/date and reconcile the same operation ID; do not generate a new identity."); }
    }

    [HttpGet("{reportId:guid}/print-attempts")]
    public async Task<IActionResult> PrintAttempts(Guid reportId, CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        if (!Service.Enabled) return Conflict("Z-Reading is disabled.");
        var report = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report == null) return NotFound();
        if (!await Allowed(report.TerminalId, false, ct)) return Forbid();
        try { return Ok(await Service.PrintAttemptsAsync(reportId, ct)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
    }

    [HttpPost("{reportId:guid}/print-attempts")]
    [HttpPost("{reportId:guid}/print-attempt")]
    public async Task<IActionResult> StartPrint(Guid reportId, [FromBody] StartZReadingPrintRequest request, CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        if (!Service.Enabled) return Conflict("Z-Reading is disabled.");
        var report = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report == null) return NotFound();
        if (!await Allowed(report.TerminalId, false, ct)) return Forbid();
        try { return Ok(await Service.StartPrintAsync(reportId, request, Operator, ct)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
        catch (DbUpdateException) { return Conflict("Print operation identity collision; retry the same request."); }
    }

    [HttpPost("{reportId:guid}/print-attempts/{attemptId:guid}/complete")]
    [HttpPost("{reportId:guid}/print-attempt/{attemptId:guid}/finish")]
    public async Task<IActionResult> CompletePrint(Guid reportId, Guid attemptId, [FromBody] CompleteZReadingPrintRequest request, CancellationToken ct = default)
    {
        if (!Supervisor) return Forbid();
        if (!Service.Enabled) return Conflict("Z-Reading is disabled.");
        var report = await db.ZReadings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report == null) return NotFound();
        if (!await Allowed(report.TerminalId, false, ct)) return Forbid();
        try { return Ok(await Service.CompletePrintAsync(reportId, attemptId, request, Operator, ct)); }
        catch (ZReadingException ex) { return StatusCode(ex.StatusCode, ex.Message); }
        catch (DbUpdateException) { return Conflict("Print completion identity collision; retry the same request."); }
    }
}
