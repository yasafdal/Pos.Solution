using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using POS.Api.Models;
using POS.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly PosDbContext _context;

        public UsersController(PosDbContext context)
        {
            _context = context;
        }

        // GET: api/Users
        [HttpGet]
        public async Task<ActionResult<IEnumerable<UserDto>>> GetUsers()
        {
            bool isAdmin = User.IsInRole("Admin");
            var users = await _context.Users
                .Select(u => new UserDto
                {
                    UserId = u.UserId,
                    Username = u.Username,
                    Role = u.Role,
                    PinCode = isAdmin ? u.PinCode : (u.PinCode != null ? "****" : null)
                })
                .ToListAsync();

            return Ok(users);
        }

        // POST: api/Users
        [HttpPost]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<ActionResult<UserDto>> CreateUser(CreateUserDto dto)
        {
            if (string.Equals(dto.Role, "Admin", StringComparison.OrdinalIgnoreCase) && !User.IsInRole("Admin"))
            {
                return Forbid("Only administrators can create admin accounts.");
            }

            // Check if username already exists
            if (await _context.Users.AnyAsync(u => u.Username.ToLower() == dto.Username.ToLower()))
            {
                return BadRequest("Username is already taken.");
            }

            // Hash the password securely using BCrypt
            string hashedPassword = BCrypt.Net.BCrypt.HashPassword(dto.Password);

            var user = new User
            {
                Username = dto.Username,
                PasswordHash = hashedPassword,
                Role = dto.Role,
                PinCode = dto.PinCode
            };

            _context.Users.Add(user);
            await _context.SaveChangesAsync();

            bool isAdmin = User.IsInRole("Admin");
            var result = new UserDto
            {
                UserId = user.UserId,
                Username = user.Username,
                Role = user.Role,
                PinCode = isAdmin ? user.PinCode : (user.PinCode != null ? "****" : null)
            };

            return CreatedAtAction(nameof(GetUsers), new { id = user.UserId }, result);
        }

        // DELETE: api/Users/5
        [HttpDelete("{id}")]
        [Authorize(Roles = "Admin,Manager")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            var user = await _context.Users.FindAsync(id);
            if (user == null) return NotFound();

            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase) && !User.IsInRole("Admin"))
            {
                return Forbid("Only administrators can delete admin accounts.");
            }

            _context.Users.Remove(user);
            await _context.SaveChangesAsync();

            return NoContent();
        }
    }
}
