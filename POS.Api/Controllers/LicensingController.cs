using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using POS.Api.Models;
using POS.Api.Services;
using POS.Shared.Models;

namespace POS.Api.Controllers;

[Route("api/licensing")]
[ApiController]
[Authorize]
public class LicensingController : ControllerBase
{
    private readonly ILicensingService _licensingService;

    [ActivatorUtilitiesConstructor]
    public LicensingController(ILicensingService licensingService)
    {
        _licensingService = licensingService;
    }

    public LicensingController(PosDbContext context, Microsoft.Extensions.Configuration.IConfiguration? config = null)
    {
        _licensingService = new LicensingService(context, config);
    }

    private bool IsAuthorizedAdmin() =>
        User?.Identity?.IsAuthenticated == true && User.IsInRole("Admin");

    private string GetActorName() =>
        User?.Identity?.Name
        ?? User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "Admin";

    private string? GetClientIp() =>
        HttpContext?.Connection?.RemoteIpAddress?.ToString();

    [HttpGet]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<TerminalLicenseAdminDto>>> GetLicenses(
        [FromQuery] int? branchId = null,
        [FromQuery] int? outletId = null,
        [FromQuery] int? terminalId = null,
        [FromQuery] string? status = null,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var list = await _licensingService.GetLicensesAsync(branchId, outletId, terminalId, status, ct);
        return Ok(list);
    }

    [HttpGet("terminal/{terminalId:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<TerminalLicenseAdminDto>> GetLicenseByTerminalId(int terminalId, CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var item = await _licensingService.GetLicenseByTerminalIdAsync(terminalId, ct);
        if (item == null) return NotFound();
        return Ok(item);
    }

    [HttpPost("issue")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> IssueLicense(
        [FromBody] IssueTerminalLicenseRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.IssueLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("renew")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> RenewLicense(
        [FromBody] RenewTerminalLicenseRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.RenewLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        if (res.ErrorCode == "IDEMPOTENCY_CONFLICT")
            return Conflict(res);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("adjust")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> AdjustLicense(
        [FromBody] RenewTerminalLicenseRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        request.IsAdjustment = true;
        var res = await _licensingService.RenewLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        if (res.ErrorCode == "IDEMPOTENCY_CONFLICT")
            return Conflict(res);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("suspend")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> SuspendLicense(
        [FromBody] ChangeLicenseStatusRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.SuspendLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("reactivate")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> ReactivateLicense(
        [FromBody] ChangeLicenseStatusRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.ReactivateLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("revoke")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> RevokeLicense(
        [FromBody] ChangeLicenseStatusRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.RevokeLicenseAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("deactivate-device")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> DeactivateDevice(
        [FromBody] ChangeLicenseStatusRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.DeactivateDeviceAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpPost("replace-device")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<LicenseOperationResponseDto>> ReplaceDevice(
        [FromBody] ReplaceLicenseDeviceRequestDto request,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        var res = await _licensingService.ReplaceDeviceAsync(request, GetActorName(), GetClientIp(), ct);
        return res.Success ? Ok(res) : BadRequest(res);
    }

    [HttpGet("history/renewals")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<LicenseRenewalHistoryDto>>> GetRenewalHistory(
        [FromQuery] int? terminalId = null,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        return Ok(await _licensingService.GetRenewalHistoryAsync(terminalId, ct));
    }

    [HttpGet("history/devices")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<LicenseDeviceHistoryDto>>> GetDeviceHistory(
        [FromQuery] int? terminalId = null,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        return Ok(await _licensingService.GetDeviceHistoryAsync(terminalId, ct));
    }

    [HttpGet("audit-logs")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<List<LicenseAuditLogDto>>> GetAuditLogs(
        [FromQuery] int? terminalId = null,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        if (!IsAuthorizedAdmin()) return Forbid();
        return Ok(await _licensingService.GetAuditLogsAsync(terminalId, limit, ct));
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth-limit")]
    [HttpPost("activate")]
    public async Task<ActionResult<LicenseOperationResponseDto>> ActivateTerminalLicense(
        [FromBody] LicenseActivateRequestDto request,
        CancellationToken ct = default)
    {
        var res = await _licensingService.ActivateLicenseAsync(request, GetClientIp(), ct);
        if (res.ErrorCode == "RATE_LIMITED")
            return StatusCode(429, res);
        return res.Success ? Ok(res) : Conflict(res);
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth-limit")]
    [HttpPost("validate")]
    public async Task<ActionResult<LicenseOperationResponseDto>> ValidateTerminalLicense(
        [FromBody] LicenseValidateRequestDto request,
        CancellationToken ct = default)
    {
        var res = await _licensingService.ValidateLicenseAsync(request, GetClientIp(), ct);
        if (res.ErrorCode == "RATE_LIMITED")
            return StatusCode(429, res);
        // Note: Return 200 OK even when license is expired/revoked/suspended so the client receives the signed status update
        if (res.LicensePackage != null || res.Evaluation != null)
            return Ok(res);
        return BadRequest(res);
    }
}
