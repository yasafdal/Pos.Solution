using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Models;
using POS.Shared.Constants;
using POS.Shared.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [EnableRateLimiting("auth-limit")]
    public class AuthController : ControllerBase
    {
        private readonly PosDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthController(PosDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [AllowAnonymous]
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return Unauthorized("Invalid username or password.");

            var user = await _context.Users.SingleOrDefaultAsync(u => u.Username == request.Username);

            try
            {
                if (user == null || !user.IsActive || !PosPermissions.IsApprovedClientRole(user.Role) || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                {
                    return Unauthorized("Invalid username or password.");
                }
            }
            catch (BCrypt.Net.SaltParseException)
            {
                return Unauthorized("Invalid username or password.");
            }

            var normalizedRole = PosPermissions.NormalizeClientRole(user.Role)!;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, normalizedRole),
                new Claim(PosPermissions.IdentityScopeClaimType, PosPermissions.ClientScope),
                new Claim(PosPermissions.OrganizationIdClaimType, user.OrganizationId.ToString())
            };

            if (request.TerminalId.HasValue)
            {
                var terminal = await _context.Terminals.AsNoTracking().Include(t => t.Outlet).ThenInclude(o => o.Branch)
                    .SingleOrDefaultAsync(t => t.TerminalId == request.TerminalId);
                if (terminal == null || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Outlet.Branch.IsActive
                    || terminal.Outlet.Branch.OrganizationId != user.OrganizationId
                    || (request.BranchId.HasValue && request.BranchId != terminal.BranchId))
                    return BadRequest("Invalid or inactive terminal ownership.");

                var lockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(_context, terminal.TerminalId, user.Username);
                if (lockState.IsCashierRestricted)
                    return StatusCode(403, lockState.CashierRestrictionMessage ?? lockState.LockMessage);
                if (lockState.IsTerminalLocked)
                    return StatusCode(423, lockState.LockMessage ?? "POS Terminal is locked for the current business day.");
                if (lockState.HasUnresolvedPreviousDayShift)
                {
                    if (!CashDrawerController.IsSupervisorRole(normalizedRole))
                        return StatusCode(409, lockState.UnresolvedShiftMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
                }
                else if (lockState.IsShiftOwnedByAnotherCashier)
                    return StatusCode(409, lockState.ActiveShiftOwnershipMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);

                claims.Add(new Claim("branch_id", terminal.BranchId.ToString()));
                claims.Add(new Claim("outlet_id", terminal.OutletId.ToString()));
                claims.Add(new Claim("terminal_id", terminal.TerminalId.ToString()));
            }

            var jwt = IssueJwtToken(claims, hoursValid: 8);
            return Ok(new { Token = jwt });
        }

        [AllowAnonymous]
        [HttpPost("pin-login")]
        public async Task<IActionResult> PinLogin([FromBody] PinLoginDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.PinCode))
            {
                return BadRequest("PIN code is required.");
            }

            var user = await _context.Users.SingleOrDefaultAsync(u => u.PinCode == request.PinCode && u.IsActive);

            if (user == null || !PosPermissions.IsApprovedClientRole(user.Role))
            {
                return Unauthorized("Invalid PIN.");
            }

            var normalizedRole = PosPermissions.NormalizeClientRole(user.Role)!;
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, normalizedRole),
                new Claim(PosPermissions.IdentityScopeClaimType, PosPermissions.ClientScope),
                new Claim(PosPermissions.OrganizationIdClaimType, user.OrganizationId.ToString())
            };

            if (request.TerminalId.HasValue)
            {
                var terminal = await _context.Terminals.AsNoTracking().Include(t => t.Outlet).ThenInclude(o => o.Branch)
                    .SingleOrDefaultAsync(t => t.TerminalId == request.TerminalId);
                if (terminal == null || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Outlet.Branch.IsActive
                    || terminal.Outlet.Branch.OrganizationId != user.OrganizationId
                    || (request.BranchId.HasValue && request.BranchId != terminal.BranchId))
                    return BadRequest("Invalid or inactive terminal ownership.");

                var lockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(_context, terminal.TerminalId, user.Username);
                if (lockState.IsCashierRestricted)
                    return StatusCode(403, lockState.CashierRestrictionMessage ?? lockState.LockMessage);
                if (lockState.IsTerminalLocked)
                    return StatusCode(423, lockState.LockMessage ?? "POS Terminal is locked for the current business day.");
                if (lockState.HasUnresolvedPreviousDayShift)
                {
                    if (!CashDrawerController.IsSupervisorRole(normalizedRole))
                        return StatusCode(409, lockState.UnresolvedShiftMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
                }
                else if (lockState.IsShiftOwnedByAnotherCashier)
                    return StatusCode(409, lockState.ActiveShiftOwnershipMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);

                claims.Add(new Claim("branch_id", terminal.BranchId.ToString()));
                claims.Add(new Claim("outlet_id", terminal.OutletId.ToString()));
                claims.Add(new Claim("terminal_id", terminal.TerminalId.ToString()));
            }

            var jwt = IssueJwtToken(claims, hoursValid: 8);
            return Ok(new { Token = jwt, Username = user.Username, Role = normalizedRole });
        }

        /// <summary>
        /// Dedicated authentication flow for platform-level AGRES System Administrators.
        /// Authenticates exclusively against AgresSystemAdmins (never the client Users table).
        /// </summary>
        [AllowAnonymous]
        [HttpPost("/api/agres/auth/login")]
        public async Task<IActionResult> AgresPlatformLogin([FromBody] LoginDto request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return Unauthorized("Invalid AGRES administrator credentials.");

            var username = request.Username.Trim();
            var admin = await _context.AgresSystemAdmins
                .SingleOrDefaultAsync(a => a.Username.ToLower() == username.ToLower());

            bool valid = false;
            try
            {
                valid = admin != null && admin.IsActive && BCrypt.Net.BCrypt.Verify(request.Password, admin.PasswordHash);
            }
            catch (BCrypt.Net.SaltParseException)
            {
                valid = false;
            }

            var ip = HttpContext?.Connection?.RemoteIpAddress?.ToString();

            if (!valid || admin == null)
            {
                _context.PlatformAuditLogs.Add(new PlatformAuditLog
                {
                    ActorUsername = username,
                    ActorSystemAdminId = admin?.SystemAdminId,
                    Action = "AGRES_LOGIN_FAILED",
                    TargetType = "AgresSystemAdmin",
                    TargetId = admin?.SystemAdminId.ToString(),
                    Details = admin == null ? "Unknown AGRES system administrator username." : "Invalid password or inactive account.",
                    IpAddress = ip,
                    CreatedAtUtc = DateTime.UtcNow
                });
                await _context.SaveChangesAsync();
                return Unauthorized("Invalid AGRES administrator credentials.");
            }

            admin.LastLoginAtUtc = DateTime.UtcNow;
            _context.PlatformAuditLogs.Add(new PlatformAuditLog
            {
                ActorUsername = admin.Username,
                ActorSystemAdminId = admin.SystemAdminId,
                Action = "AGRES_LOGIN_SUCCESS",
                TargetType = "AgresSystemAdmin",
                TargetId = admin.SystemAdminId.ToString(),
                Details = "Platform administrator authenticated.",
                IpAddress = ip,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _context.SaveChangesAsync();

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, $"agres:{admin.SystemAdminId}"),
                new Claim(PosPermissions.AgresAdminIdClaimType, admin.SystemAdminId.ToString()),
                new Claim(ClaimTypes.Name, admin.Username),
                new Claim(ClaimTypes.Role, PosPermissions.AgresSystemAdminRole),
                new Claim(PosPermissions.IdentityScopeClaimType, PosPermissions.PlatformScope)
            };

            var jwt = IssueJwtToken(claims, hoursValid: 4);
            return Ok(new { Token = jwt, Username = admin.Username, DisplayName = admin.DisplayName, Scope = PosPermissions.PlatformScope });
        }

        private string IssueJwtToken(IEnumerable<Claim> claims, int hoursValid)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(hoursValid),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
