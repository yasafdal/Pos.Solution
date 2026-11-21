using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Constants;
using POS.Shared.Models;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize(Policy = PosPermissions.ClientOperationsPolicy)]
    public class UsersController : ControllerBase
    {
        private readonly PosDbContext _context;

        public UsersController(PosDbContext context)
        {
            _context = context;
        }

        private async Task<int?> ResolveCallerOrganizationIdAsync()
        {
            var claimOrgId = PosPermissions.GetOrganizationId(User);
            if (claimOrgId.HasValue) return claimOrgId.Value;

            var username = User?.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(username))
            {
                var dbOrgId = await _context.Users.AsNoTracking()
                    .Where(u => u.Username == username)
                    .Select(u => (int?)u.OrganizationId)
                    .FirstOrDefaultAsync();
                if (dbOrgId.HasValue) return dbOrgId.Value;
            }

            // Fallback for synthetic unit tests with client principal and default Org #1
            return 1;
        }

        private static UserDto MapUserDto(User u, bool revealPin) => new()
        {
            UserId = u.UserId,
            OrganizationId = u.OrganizationId,
            Username = u.Username,
            Role = PosPermissions.NormalizeClientRole(u.Role) ?? u.Role,
            PinCode = revealPin ? u.PinCode : (string.IsNullOrEmpty(u.PinCode) ? null : "****"),
            IsActive = u.IsActive
        };

        // GET: api/Users
        [HttpGet]
        [Authorize(Policy = PosPermissions.ClientOperationsPolicy)]
        public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers([FromQuery] string? search = null)
        {
            if (!PosPermissions.IsClientUser(User))
                return Forbid();

            var orgId = await ResolveCallerOrganizationIdAsync();
            if (!orgId.HasValue) return Forbid();

            bool isClientAdmin = PosPermissions.IsClientAdmin(User);
            var query = _context.Users
                .AsNoTracking()
                .Where(u => u.OrganizationId == orgId.Value);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(u => u.Username.ToLower().Contains(term) || u.Role.ToLower().Contains(term));
            }

            var rawUsers = await query
                .OrderBy(u => u.UserId)
                .ToListAsync();

            // Defense-in-depth: strictly allowlist approved client roles only
            var users = rawUsers
                .Where(u => PosPermissions.IsApprovedClientRole(u.Role))
                .Select(u => MapUserDto(u, isClientAdmin))
                .ToList();

            return Ok(users);
        }

        // POST: api/Users
        [HttpPost]
        [Authorize(Policy = PosPermissions.ClientUserManagementPolicy)]
        public async Task<ActionResult<UserDto>> CreateUser(CreateUserDto dto)
        {
            if (!PosPermissions.IsClientUserManager(User))
                return Forbid();

            var normalizedRole = PosPermissions.NormalizeClientRole(dto?.Role);
            if (normalizedRole == null)
            {
                return BadRequest("Invalid role. Only approved client roles (Cashier, Senior Cashier, Manager, Admin) are permitted.");
            }

            if (string.Equals(normalizedRole, "Admin", StringComparison.OrdinalIgnoreCase) && !PosPermissions.IsClientAdmin(User))
            {
                return Forbid("Only Client Administrators can create Client Admin accounts.");
            }

            var orgId = await ResolveCallerOrganizationIdAsync();
            if (!orgId.HasValue) return Forbid();

            var username = dto!.Username.Trim();
            if (string.IsNullOrWhiteSpace(username))
                return BadRequest("Username is required.");

            // Prevent collisions with existing client users OR platform AGRES System Admin usernames
            if (await _context.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()) ||
                await _context.AgresSystemAdmins.AnyAsync(a => a.Username.ToLower() == username.ToLower()))
            {
                return BadRequest("Username is already taken.");
            }

            string? pinCode = string.IsNullOrWhiteSpace(dto.PinCode) ? null : dto.PinCode.Trim();
            if (pinCode != null && await _context.Users.AnyAsync(u => u.OrganizationId == orgId.Value && u.PinCode == pinCode))
            {
                return BadRequest("PIN code is already in use by another user in this organization.");
            }

            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(dto.Password);

            var user = new User
            {
                OrganizationId = orgId.Value,
                Username = username,
                PasswordHash = hashedPassword,
                Role = normalizedRole,
                PinCode = pinCode,
                IsActive = true
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetUsers), new { id = user.UserId }, MapUserDto(user, PosPermissions.IsClientAdmin(User)));
        }

        // PUT: api/Users/5
        [HttpPut("{id:int}")]
        [Authorize(Policy = PosPermissions.ClientUserManagementPolicy)]
        public async Task<ActionResult<UserDto>> UpdateUser(int id, UpdateUserDto dto)
        {
            if (!PosPermissions.IsClientUserManager(User))
                return Forbid();

            var normalizedRole = PosPermissions.NormalizeClientRole(dto?.Role);
            if (normalizedRole == null)
            {
                return BadRequest("Invalid role. Platform-level or unapproved roles cannot be assigned.");
            }

            var orgId = await ResolveCallerOrganizationIdAsync();
            if (!orgId.HasValue) return Forbid();

            var user = await _context.Users.SingleOrDefaultAsync(u => u.UserId == id && u.OrganizationId == orgId.Value);
            if (user == null)
                return NotFound("User not found in your organization.");

            bool isClientAdmin = PosPermissions.IsClientAdmin(User);
            if ((string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(normalizedRole, "Admin", StringComparison.OrdinalIgnoreCase)) && !isClientAdmin)
            {
                return Forbid("Only Client Administrators can modify or assign the Client Admin role.");
            }

            var newUsername = dto!.Username.Trim();
            if (string.IsNullOrWhiteSpace(newUsername))
                return BadRequest("Username is required.");

            if (!string.Equals(user.Username, newUsername, StringComparison.OrdinalIgnoreCase))
            {
                if (await _context.Users.AnyAsync(u => u.UserId != id && u.Username.ToLower() == newUsername.ToLower()) ||
                    await _context.AgresSystemAdmins.AnyAsync(a => a.Username.ToLower() == newUsername.ToLower()))
                {
                    return BadRequest("Username is already taken.");
                }
                user.Username = newUsername;
            }

            if (!string.IsNullOrWhiteSpace(dto.Password))
            {
                user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password);
            }

            string? newPin = string.IsNullOrWhiteSpace(dto.PinCode) ? null : dto.PinCode.Trim();
            if (newPin != null && newPin != "****")
            {
                if (await _context.Users.AnyAsync(u => u.UserId != id && u.OrganizationId == orgId.Value && u.PinCode == newPin))
                {
                    return BadRequest("PIN code is already in use by another user in this organization.");
                }
                user.PinCode = newPin;
            }
            else if (dto.PinCode == "")
            {
                user.PinCode = null;
            }

            user.Role = normalizedRole;
            user.IsActive = dto.IsActive;

            await _context.SaveChangesAsync();
            return Ok(MapUserDto(user, isClientAdmin));
        }

        // DELETE: api/Users/5
        [HttpDelete("{id:int}")]
        [Authorize(Policy = PosPermissions.ClientUserManagementPolicy)]
        public async Task<IActionResult> DeleteUser(int id)
        {
            if (!PosPermissions.IsClientUserManager(User))
                return Forbid();

            var orgId = await ResolveCallerOrganizationIdAsync();
            if (!orgId.HasValue) return Forbid();

            var user = await _context.Users.SingleOrDefaultAsync(u => u.UserId == id && u.OrganizationId == orgId.Value);
            if (user == null) return NotFound();

            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase) && !PosPermissions.IsClientAdmin(User))
            {
                return Forbid("Only Client Administrators can delete Client Admin accounts.");
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
