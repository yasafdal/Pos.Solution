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
    public class TerminalsController : ControllerBase
    {
        private readonly PosDbContext _context;
        private readonly IConfiguration _configuration;

        public TerminalsController(PosDbContext context, IConfiguration configuration)
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
            var outlets = await _context.Terminals.AsNoTracking()
                .Where(o => o.DeviceIdentifier != null || o.IpAddress != null)
                .Select(o => new { o.TerminalId, o.BranchId, o.OutletId, o.TerminalCode, o.IpAddress, o.Ptunumber, o.Minnumber, o.SerialNumber, OutletName = o.Outlet.Name, OutletActive = o.Outlet.IsActive, o.Name, o.DeviceIdentifier,
                    o.IsActive, BranchActive = o.Branch.IsActive,
                    BranchName = o.Branch.Name, o.Branch.Code,
                    BranchAddress = o.Branch.Address, BranchPhone = o.Branch.Phone,
                    o.Branch.BusinessTypeId,
                    BusinessType = o.Branch.BusinessType == null ? null : o.Branch.BusinessType.Type })
                .ToListAsync();
            var matches = outlets.Where(o => MatchesIp(o.IpAddress, NormalizeIp(address)) || MatchesIp(o.DeviceIdentifier, NormalizeIp(address))).ToList();
            if (matches.Count == 0) return NotFound("This IP address is not listed");
            if (matches.Count != 1) return Conflict("This IP address is assigned to multiple terminals. Contact an administrator.");
            var outlet = matches[0];
            if (!outlet.IsActive || !outlet.OutletActive || !outlet.BranchActive)
                return Conflict("This terminal, outlet or branch is inactive. Contact an administrator.");
            if (string.IsNullOrWhiteSpace(outlet.Code))
                return Conflict("The terminal's branch code is not configured. Contact an administrator.");
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
                    new Claim("terminal_id", outlet.TerminalId.ToString()),
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

            return Ok(new TerminalConfigDto
            {
                TerminalId = outlet.TerminalId,
                OutletId = outlet.OutletId, OutletName = outlet.OutletName, TerminalCode = outlet.TerminalCode,
                DeviceIdentifier = outlet.DeviceIdentifier, IpAddress = outlet.IpAddress,
                PTUNumber = outlet.Ptunumber, MINNumber = outlet.Minnumber, SerialNumber = outlet.SerialNumber,
                BranchId = outlet.BranchId,
                BranchName = outlet.BranchName,
                BranchCode = outlet.Code,
                BranchAddress = outlet.BranchAddress,
                BranchPhone = outlet.BranchPhone,
                TerminalName = outlet.Name,
                BusinessTypeId = outlet.BusinessTypeId,
                BusinessType = outlet.BusinessType,
                Token = token
            });
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpGet]
        public async Task<ActionResult<List<TerminalDto>>> GetTerminals([FromQuery] int? outletId, [FromQuery] int? branchId)
        {
            var query = _context.Terminals.AsNoTracking().Include(t => t.Outlet).Include(t => t.Branch).AsQueryable();
            if (outletId.HasValue) query = query.Where(t => t.OutletId == outletId);
            if (branchId.HasValue) query = query.Where(t => t.BranchId == branchId);
            return Ok((await query.OrderBy(t => t.TerminalCode).ToListAsync()).Select(HierarchyMapping.ToDto).ToList());
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpGet("{id:int}")]
        public async Task<ActionResult<TerminalDto>> GetTerminal(int id)
        {
            var terminal = await _context.Terminals.AsNoTracking().Include(t => t.Outlet).Include(t => t.Branch).SingleOrDefaultAsync(t => t.TerminalId == id);
            return terminal == null ? NotFound() : Ok(HierarchyMapping.ToDto(terminal));
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpPost]
        public async Task<ActionResult<TerminalDto>> CreateTerminal(TerminalDto dto)
        {
            var error = await ValidateTerminal(dto, 0);
            if (error != null) return BadRequest(error);
            var terminal = new Terminal { CreatedAt = DateTime.UtcNow };
            ApplyTerminal(terminal, dto);
            _context.Terminals.Add(terminal);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException) { return Conflict("Terminal code, device identifier or IP address is already registered. Refresh before retrying."); }
            return CreatedAtAction(nameof(GetTerminal), new { id = terminal.TerminalId }, HierarchyMapping.ToDto(terminal));
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpPut("{id:int}")]
        public async Task<IActionResult> UpdateTerminal(int id, TerminalDto dto)
        {
            var terminal = await _context.Terminals.FindAsync(id);
            if (terminal == null) return NotFound();
            var error = await ValidateTerminal(dto, id);
            if (error != null) return BadRequest(error);
            if ((terminal.OutletId != dto.OutletId || terminal.BranchId != dto.BranchId) && await HasHistory(id))
                return Conflict("A terminal with transaction or invoice history cannot be reassigned to another outlet. Create a new terminal.");
            ApplyTerminal(terminal, dto);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException) { return Conflict("Terminal code, device identifier or IP address is already registered. Refresh before retrying."); }
            return NoContent();
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpDelete("{id:int}")]
        public async Task<IActionResult> DeleteTerminal(int id)
        {
            var terminal = await _context.Terminals.FindAsync(id);
            if (terminal == null) return NotFound();
            if (await HasHistory(id)) return Conflict("Terminal has historical records or invoice series. Deactivate it instead.");
            _context.Terminals.Remove(terminal);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException) { return Conflict("Terminal has related records and cannot be deleted."); }
            return NoContent();
        }

        private async Task<bool> HasHistory(int id) =>
            await _context.TerminalRecoveryLogs.AnyAsync(x => x.TerminalId == id) ||
            await _context.Orders.AnyAsync(x => x.TerminalId == id) ||
            await _context.InvoiceSequences.AnyAsync(x => x.TerminalId == id) ||
            await _context.OrderSequences.AnyAsync(x => x.TerminalId == id) ||
            await _context.CashDrawerShifts.AnyAsync(x => x.TerminalId == id) ||
            await _context.CashDrawerMovements.AnyAsync(x => x.TerminalId == id) ||
            await _context.CashierSessions.AnyAsync(x => x.TerminalId == id) ||
            await _context.CashierSwitchLogs.AnyAsync(x => x.TerminalId == id) ||
            await _context.HeldSales.AnyAsync(x => x.TerminalId == id) ||
            await _context.HeldSaleAuditLogs.AnyAsync(x => x.TerminalId == id) ||
            await _context.ReceiptReprintLogs.AnyAsync(x => x.TerminalId == id) ||
            await _context.SalesReturns.AnyAsync(x => x.TerminalId == id);

        private async Task<string?> ValidateTerminal(TerminalDto dto, int id)
        {
            dto.Name = dto.Name.Trim();
            dto.TerminalCode = dto.TerminalCode.Trim().ToUpperInvariant();
            if (dto.Name.Length is < 1 or > 100 || dto.TerminalCode.Length is < 1 or > 30)
                return "Terminal code and name are required and must fit their maximum lengths.";
            var outlet = await _context.Outlets.AsNoTracking().Include(o => o.Branch).SingleOrDefaultAsync(o => o.OutletId == dto.OutletId);
            if (outlet == null || !outlet.IsActive || !outlet.Branch.IsActive) return "Select an active outlet belonging to an active branch.";
            if (dto.BranchId != 0 && dto.BranchId != outlet.BranchId) return "Terminal branch does not match its outlet.";
            dto.BranchId = outlet.BranchId;
            dto.DeviceIdentifier = string.IsNullOrWhiteSpace(dto.DeviceIdentifier) ? null : dto.DeviceIdentifier.Trim().ToLowerInvariant();
            if (IPAddress.TryParse(dto.DeviceIdentifier, out var deviceIp)) dto.DeviceIdentifier = NormalizeIp(deviceIp);
            if (dto.DeviceIdentifier?.Length > 100) return "Device identifier is too long.";
            if (IPAddress.TryParse(dto.DeviceIdentifier, out var identityAddress)
                && (!IsUnicast(identityAddress) || (!string.IsNullOrWhiteSpace(dto.IpAddress)
                    && (!IPAddress.TryParse(dto.IpAddress, out var selectedAddress) || NormalizeIp(identityAddress) != NormalizeIp(selectedAddress)))))
                return "An IP-valued device identifier must match the terminal's non-loopback IP address.";
            if (!string.IsNullOrWhiteSpace(dto.IpAddress))
            {
                if (!IPAddress.TryParse(dto.IpAddress.Trim(), out var ip) || !IsUnicast(ip)) return "IP address must be a valid non-loopback unicast address.";
                dto.IpAddress = NormalizeIp(ip);
            }
            else dto.IpAddress = IPAddress.TryParse(dto.DeviceIdentifier, out var ip) && IsUnicast(ip) ? NormalizeIp(ip) : null;
            if (new[] { dto.PTUNumber, dto.MINNumber, dto.SerialNumber }.Any(x => x?.Length > 50)) return "PTU, MIN and serial number must not exceed 50 characters.";
            var existing = await _context.Terminals.AsNoTracking().Where(t => t.TerminalId != id).ToListAsync();
            if (existing.Any(t => t.OutletId == dto.OutletId && t.TerminalCode.Equals(dto.TerminalCode, StringComparison.OrdinalIgnoreCase))) return "Terminal code must be unique within the outlet.";
            if (dto.DeviceIdentifier != null && existing.Any(t => string.Equals(t.DeviceIdentifier, dto.DeviceIdentifier, StringComparison.OrdinalIgnoreCase))) return "Device identifier is already registered.";
            if (dto.IpAddress != null && existing.Any(t => MatchesIp(t.IpAddress, dto.IpAddress) || MatchesIp(t.DeviceIdentifier, dto.IpAddress))) return "IP address is already registered.";
            return null;
        }

        private static bool IsUnicast(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            return !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any)
                && !ip.Equals(IPAddress.IPv6Any) && !ip.Equals(IPAddress.Broadcast) && !ip.IsIPv6Multicast
                && !(ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224);
        }
        private static string NormalizeIp(IPAddress ip) => (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
        private static bool MatchesIp(string? value, string expected) => IPAddress.TryParse(value, out var ip) && NormalizeIp(ip) == expected;

        private static void ApplyTerminal(Terminal t, TerminalDto dto)
        {
            t.OutletId = dto.OutletId; t.BranchId = dto.BranchId; t.TerminalCode = dto.TerminalCode;
            t.Name = dto.Name; t.DeviceIdentifier = dto.DeviceIdentifier; t.IpAddress = dto.IpAddress;
            t.Ptunumber = dto.PTUNumber; t.Minnumber = dto.MINNumber; t.SerialNumber = dto.SerialNumber;
            if (t.IsActive && !dto.IsActive)
            {
                t.DeactivatedAt ??= DateTime.UtcNow;
            }
            else if (!t.IsActive && dto.IsActive)
            {
                t.DeactivatedAt = null;
            }
            t.IsActive = dto.IsActive; t.UpdatedAt = DateTime.UtcNow;
        }
    }
}

