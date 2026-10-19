using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using POS.Api.Models;
using POS.Shared.Models;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly PosDbContext _context;
        private readonly IConfiguration _configuration;

        public AuthController(PosDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginDto request)
        {
            var user = await _context.Users.SingleOrDefaultAsync(u => u.Username == request.Username);

            try
            {
                // Verify User exists and password matches the BCrypt hash
                if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.PasswordHash))
                {
                    return Unauthorized("Invalid username or password.");
                }
            }
            catch (BCrypt.Net.SaltParseException)
            {
                return Unauthorized("Invalid username or password.");
            }

            // Create the Token Claims (Data inside the token)
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            };

            if (request.BranchId.HasValue)
                claims.Add(new Claim("branch_id", request.BranchId.Value.ToString()));
            if (request.OutletId.HasValue)
                claims.Add(new Claim("outlet_id", request.OutletId.Value.ToString()));

            // Generate Token
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(8), // Shift length
                signingCredentials: creds
            );

            var jwt = new JwtSecurityTokenHandler().WriteToken(token);

            return Ok(new { Token = jwt });
        }

        [HttpPost("pin-login")]
        public async Task<IActionResult> PinLogin([FromBody] PinLoginDto request)
        {
            if (string.IsNullOrWhiteSpace(request?.PinCode))
            {
                return BadRequest("PIN code is required.");
            }

            var user = await _context.Users.SingleOrDefaultAsync(u => u.PinCode == request.PinCode);

            if (user == null)
            {
                return Unauthorized("Invalid PIN.");
            }

            // Create the Token Claims
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role)
            };

            if (request.BranchId.HasValue)
                claims.Add(new Claim("branch_id", request.BranchId.Value.ToString()));
            if (request.OutletId.HasValue)
                claims.Add(new Claim("outlet_id", request.OutletId.Value.ToString()));

            // Generate Token
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.Now.AddHours(8),
                signingCredentials: creds
            );

            var jwt = new JwtSecurityTokenHandler().WriteToken(token);

            return Ok(new { Token = jwt, Username = user.Username, Role = user.Role });
        }

    }
}
