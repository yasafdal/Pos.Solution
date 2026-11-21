using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Constants;
using POS.Shared.Models;
using System.Security.Claims;

namespace POS.Api.Controllers;

/// <summary>
/// Platform-level administration endpoints strictly restricted to AGRES System Administrators.
/// Manages Client Organizations, AGRES System Admin identities (separate from client Users), and Platform Audit Logs.
/// </summary>
[Route("api/agres")]
[ApiController]
[Authorize(Policy = PosPermissions.AgresSystemAdminPolicy)]
public class AgresPlatformController(PosDbContext context) : ControllerBase
{
    private int? GetActorAdminId()
    {
        var raw = User?.FindFirst(PosPermissions.AgresAdminIdClaimType)?.Value;
        return int.TryParse(raw, out var id) ? id : null;
    }

    private string GetActorUsername() =>
        User?.Identity?.Name ?? User?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "AgresSystemAdmin";

    private string? GetClientIp() =>
        HttpContext?.Connection?.RemoteIpAddress?.ToString();

    private void AddAudit(string action, string targetType, string? targetId, string details)
    {
        context.PlatformAuditLogs.Add(new PlatformAuditLog
        {
            ActorUsername = GetActorUsername(),
            ActorSystemAdminId = GetActorAdminId(),
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details,
            IpAddress = GetClientIp(),
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    // ── Organizations ───────────────────────────────────────────────────────
    [HttpGet("organizations")]
    public async Task<ActionResult<List<OrganizationDto>>> GetOrganizations()
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();

        var orgs = await context.Organizations
            .AsNoTracking()
            .OrderBy(o => o.OrganizationId)
            .Select(o => new OrganizationDto
            {
                OrganizationId = o.OrganizationId,
                Code = o.Code,
                Name = o.Name,
                IsActive = o.IsActive,
                CreatedAtUtc = o.CreatedAtUtc,
                BranchCount = o.Branches.Count,
                UserCount = o.Users.Count
            })
            .ToListAsync();

        return Ok(orgs);
    }

    [HttpPost("organizations")]
    public async Task<ActionResult<OrganizationDto>> CreateOrganization([FromBody] CreateOrganizationDto dto)
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();
        if (dto == null || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Organization code and name are required.");

        var code = dto.Code.Trim().ToUpperInvariant();
        var name = dto.Name.Trim();

        if (await context.Organizations.AnyAsync(o => o.Code.ToUpper() == code))
            return BadRequest("Organization code already exists.");

        var org = new Organization
        {
            Code = code,
            Name = name,
            IsActive = dto.IsActive,
            CreatedAtUtc = DateTime.UtcNow
        };

        context.Organizations.Add(org);
        await context.SaveChangesAsync();

        AddAudit("ORGANIZATION_CREATED", "Organization", org.OrganizationId.ToString(), $"Created organization {org.Code} ({org.Name}).");
        await context.SaveChangesAsync();

        return Ok(new OrganizationDto
        {
            OrganizationId = org.OrganizationId,
            Code = org.Code,
            Name = org.Name,
            IsActive = org.IsActive,
            CreatedAtUtc = org.CreatedAtUtc
        });
    }

    [HttpPut("organizations/{id:int}")]
    public async Task<ActionResult<OrganizationDto>> UpdateOrganization(int id, [FromBody] CreateOrganizationDto dto)
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();
        if (dto == null || string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Organization code and name are required.");

        var org = await context.Organizations.FindAsync(id);
        if (org == null) return NotFound();

        var code = dto.Code.Trim().ToUpperInvariant();
        if (await context.Organizations.AnyAsync(o => o.OrganizationId != id && o.Code.ToUpper() == code))
            return BadRequest("Organization code already exists.");

        org.Code = code;
        org.Name = dto.Name.Trim();
        org.IsActive = dto.IsActive;

        AddAudit("ORGANIZATION_UPDATED", "Organization", org.OrganizationId.ToString(), $"Updated organization {org.Code} ({org.Name}), IsActive={org.IsActive}.");
        await context.SaveChangesAsync();

        return Ok(new OrganizationDto
        {
            OrganizationId = org.OrganizationId,
            Code = org.Code,
            Name = org.Name,
            IsActive = org.IsActive,
            CreatedAtUtc = org.CreatedAtUtc
        });
    }

    // ── AGRES System Admins ─────────────────────────────────────────────────
    [HttpGet("admins")]
    public async Task<ActionResult<List<AgresSystemAdminDto>>> GetSystemAdmins()
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();

        var admins = await context.AgresSystemAdmins
            .AsNoTracking()
            .OrderBy(a => a.SystemAdminId)
            .Select(a => new AgresSystemAdminDto
            {
                SystemAdminId = a.SystemAdminId,
                Username = a.Username,
                DisplayName = a.DisplayName,
                IsActive = a.IsActive,
                CreatedAtUtc = a.CreatedAtUtc,
                LastLoginAtUtc = a.LastLoginAtUtc,
                CreatedBy = a.CreatedBy
            })
            .ToListAsync();

        return Ok(admins);
    }

    [HttpPost("admins")]
    public async Task<ActionResult<AgresSystemAdminDto>> CreateSystemAdmin([FromBody] CreateAgresSystemAdminDto dto)
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();
        if (dto == null || string.IsNullOrWhiteSpace(dto.Username) || string.IsNullOrWhiteSpace(dto.Password))
            return BadRequest("Username and password are required.");

        var username = dto.Username.Trim();
        if (await context.AgresSystemAdmins.AnyAsync(a => a.Username.ToLower() == username.ToLower()) ||
            await context.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
        {
            return BadRequest("Username is already in use.");
        }

        var admin = new AgresSystemAdmin
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? username : dto.DisplayName.Trim(),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password),
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = GetActorUsername()
        };

        context.AgresSystemAdmins.Add(admin);
        await context.SaveChangesAsync();

        AddAudit("SYSTEM_ADMIN_CREATED", "AgresSystemAdmin", admin.SystemAdminId.ToString(), $"Created AGRES System Admin '{admin.Username}'.");
        await context.SaveChangesAsync();

        return Ok(new AgresSystemAdminDto
        {
            SystemAdminId = admin.SystemAdminId,
            Username = admin.Username,
            DisplayName = admin.DisplayName,
            IsActive = admin.IsActive,
            CreatedAtUtc = admin.CreatedAtUtc,
            CreatedBy = admin.CreatedBy
        });
    }

    [HttpPut("admins/{id:int}")]
    public async Task<ActionResult<AgresSystemAdminDto>> UpdateSystemAdmin(int id, [FromBody] UpdateAgresSystemAdminDto dto)
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();

        var admin = await context.AgresSystemAdmins.FindAsync(id);
        if (admin == null) return NotFound();

        if (!dto.IsActive && GetActorAdminId() == admin.SystemAdminId)
            return BadRequest("You cannot deactivate your own active AGRES System Admin account.");

        admin.DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? admin.DisplayName : dto.DisplayName.Trim();
        admin.IsActive = dto.IsActive;
        if (!string.IsNullOrWhiteSpace(dto.Password))
        {
            admin.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
        }

        AddAudit("SYSTEM_ADMIN_UPDATED", "AgresSystemAdmin", admin.SystemAdminId.ToString(), $"Updated AGRES System Admin '{admin.Username}', IsActive={admin.IsActive}.");
        await context.SaveChangesAsync();

        return Ok(new AgresSystemAdminDto
        {
            SystemAdminId = admin.SystemAdminId,
            Username = admin.Username,
            DisplayName = admin.DisplayName,
            IsActive = admin.IsActive,
            CreatedAtUtc = admin.CreatedAtUtc,
            LastLoginAtUtc = admin.LastLoginAtUtc,
            CreatedBy = admin.CreatedBy
        });
    }

    // ── Platform Audit Logs ─────────────────────────────────────────────────
    [HttpGet("audit-logs")]
    public async Task<ActionResult<List<PlatformAuditLogDto>>> GetAuditLogs([FromQuery] int limit = 100)
    {
        if (!PosPermissions.IsAgresSystemAdmin(User)) return Forbid();

        int take = Math.Clamp(limit, 1, 500);
        var logs = await context.PlatformAuditLogs
            .AsNoTracking()
            .OrderByDescending(l => l.AuditLogId)
            .Take(take)
            .Select(l => new PlatformAuditLogDto
            {
                AuditLogId = l.AuditLogId,
                ActorUsername = l.ActorUsername,
                ActorSystemAdminId = l.ActorSystemAdminId,
                Action = l.Action,
                TargetType = l.TargetType,
                TargetId = l.TargetId,
                Details = l.Details,
                IpAddress = l.IpAddress,
                CreatedAtUtc = l.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(logs);
    }
}
