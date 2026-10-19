using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Net;
using POS.Api.Models;
using POS.Shared.Models;

using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace POS.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class OutletsController : ControllerBase
    {
        private readonly PosDbContext _context;
        private readonly IConfiguration _configuration;

        public OutletsController(PosDbContext context, IConfiguration configuration)
        {
            _context = context;
            _configuration = configuration;
        }

        // The JWT authenticates the cashier. The supplied device IP only identifies
        // the terminal; it is not an authentication or authorization credential.
        [Authorize]
        [HttpGet("resolve-terminal")]
        public async Task<IActionResult> ResolveTerminal([FromQuery] string ipAddress)
        {
            if (!IPAddress.TryParse(ipAddress, out var address) || IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                return BadRequest("A valid non-loopback terminal IP address is required.");

            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
                || address.Equals(IPAddress.Broadcast) || address.IsIPv6Multicast
                || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && address.GetAddressBytes()[0] >= 224))
                return BadRequest("A unicast terminal IP address is required.");
            // Parse both sides so equivalent IPv6 spellings cannot hide duplicates.
            // This is a read-only lookup; no schema or stored identifier is changed.
            var outlets = await _context.Outlets.AsNoTracking()
                .Where(o => o.DeviceIdentifier != null)
                .Select(o => new { o.OutletId, o.BranchId, o.Name, o.DeviceIdentifier,
                    o.IsActive, BranchActive = o.Branch.IsActive,
                    BranchName = o.Branch.Name, o.Branch.Code,
                    BranchAddress = o.Branch.Address, BranchPhone = o.Branch.Phone,
                    o.Branch.BusinessTypeId,
                    BusinessType = o.Branch.BusinessType == null ? null : o.Branch.BusinessType.Type })
                .ToListAsync();
            var matches = outlets.Where(o => IPAddress.TryParse(o.DeviceIdentifier?.Trim(), out var stored)
                && (stored.IsIPv4MappedToIPv6 ? stored.MapToIPv4() : stored).Equals(address)).ToList();
            if (matches.Count == 0) return NotFound("This IP address is not listed");
            if (matches.Count != 1) return Conflict("This IP address is assigned to multiple outlets. Contact an administrator.");
            var outlet = matches[0];
            if (!outlet.IsActive || !outlet.BranchActive)
                return Conflict("This outlet or branch is inactive. Contact an administrator.");
            if (string.IsNullOrWhiteSpace(outlet.Code))
                return Conflict("The outlet's branch code is not configured. Contact an administrator.");
            string? token = null;
            var jwtKey = _configuration["Jwt:Key"];
            if (!string.IsNullOrEmpty(jwtKey) && User.Identity?.IsAuthenticated == true)
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0"),
                    new Claim(ClaimTypes.Name, User.Identity.Name ?? ""),
                    new Claim(ClaimTypes.Role, User.FindFirst(ClaimTypes.Role)?.Value ?? "Cashier"),
                    new Claim("branch_id", outlet.BranchId.ToString()),
                    new Claim("outlet_id", outlet.OutletId.ToString())
                };

                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
                var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

                var jwt = new JwtSecurityToken(
                    issuer: _configuration["Jwt:Issuer"],
                    audience: _configuration["Jwt:Audience"],
                    claims: claims,
                    expires: DateTime.Now.AddHours(8),
                    signingCredentials: creds
                );

                token = new JwtSecurityTokenHandler().WriteToken(jwt);
            }

            return Ok(new TerminalOutletDto
            {
                OutletId = outlet.OutletId,
                BranchId = outlet.BranchId,
                BranchName = outlet.BranchName,
                BranchCode = outlet.Code,
                BranchAddress = outlet.BranchAddress,
                BranchPhone = outlet.BranchPhone,
                OutletName = outlet.Name,
                BusinessTypeId = outlet.BusinessTypeId,
                BusinessType = outlet.BusinessType,
                Token = token
            });
        }

        [HttpPost]
        public async Task<ActionResult<OutletDto>> CreateOutlet(OutletDto dto)
        {
            var outlet = new Outlet
            {
                BranchId = dto.BranchId,
                Name = dto.Name,
                DeviceIdentifier = dto.DeviceIdentifier,
                Ptunumber = dto.PTUNumber,
                Minnumber = dto.MINNumber,
                SerialNumber = dto.SerialNumber,
                IsActive = dto.IsActive
            };

            _context.Outlets.Add(outlet);
            await _context.SaveChangesAsync();

            dto.OutletId = outlet.OutletId;
            return Ok(dto);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateOutlet(int id, OutletDto dto)
        {
            var outlet = await _context.Outlets.FindAsync(id);
            if (outlet == null) return NotFound();

            outlet.Name = dto.Name;
            outlet.DeviceIdentifier = dto.DeviceIdentifier;
            outlet.Ptunumber = dto.PTUNumber;
            outlet.Minnumber = dto.MINNumber;
            outlet.SerialNumber = dto.SerialNumber;
            outlet.IsActive = dto.IsActive;

            await _context.SaveChangesAsync();
            return NoContent();
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteOutlet(int id)
        {
            var outlet = await _context.Outlets.FindAsync(id);
            if (outlet == null) return NotFound();

            _context.Outlets.Remove(outlet);
            await _context.SaveChangesAsync();
            return NoContent();
        }

    }
}
