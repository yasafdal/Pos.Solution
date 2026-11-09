using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using System.Net;
using POS.Api.Models;
using POS.Shared.Models;
using System.Security.Cryptography;

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
        public async Task<IActionResult> ResolveTerminal([FromQuery] string? ipAddress = null, [FromQuery] string? deviceUuid = null)
        {
            var query = _context.Terminals.AsNoTracking()
                .Select(o => new { o.TerminalId, o.BranchId, o.OutletId, o.TerminalCode, o.IpAddress, o.Ptunumber, o.Minnumber, o.SerialNumber, OutletName = o.Outlet.Name, OutletActive = o.Outlet.IsActive, o.Name, o.DeviceIdentifier,
                    o.IsActive, o.MenuDisplayMode, BranchActive = o.Branch.IsActive,
                    BranchName = o.Branch.Name, o.Branch.Code,
                    BranchAddress = o.Branch.Address, BranchPhone = o.Branch.Phone,
                    o.Branch.BusinessTypeId,
                    BusinessType = o.Branch.BusinessType == null ? null : o.Branch.BusinessType.Type,
                    o.Uuid, o.AllowSync });

            var matchedByUuid = !string.IsNullOrWhiteSpace(deviceUuid)
                ? await query.Where(o => o.Uuid == deviceUuid.Trim()).ToListAsync()
                : new();

            if (matchedByUuid.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(deviceUuid) && string.IsNullOrWhiteSpace(ipAddress))
                    return NotFound("This device UUID is not paired to an active terminal.");

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
                var outlets = await query
                    .Where(o => o.DeviceIdentifier != null || o.IpAddress != null)
                    .ToListAsync();
                matchedByUuid = outlets.Where(o => MatchesIp(o.IpAddress, NormalizeIp(address)) || MatchesIp(o.DeviceIdentifier, NormalizeIp(address))).ToList();
                if (matchedByUuid.Count == 0) return NotFound("This IP address is not listed");
                if (matchedByUuid.Count != 1) return Conflict("This IP address is assigned to multiple terminals. Contact an administrator.");
            }

            var outlet = matchedByUuid[0];
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
                    new Claim("outlet_id", outlet.OutletId.ToString()),
                    new Claim("terminal_uuid", outlet.Uuid ?? string.Empty)
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
                TerminalId = outlet.TerminalId, MenuDisplayMode = outlet.MenuDisplayMode,
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
                Token = token,
                Uuid = outlet.Uuid,
                AllowSync = outlet.AllowSync
            });
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpGet]
        public async Task<ActionResult<List<TerminalDto>>> GetTerminals([FromQuery] int? outletId, [FromQuery] int? branchId)
        {
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId))
            {
                if (branchId.HasValue && branchId.Value != supBranchId) return Forbid();
                branchId = supBranchId;
            }

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
            if (terminal == null) return NotFound();
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && terminal.BranchId != supBranchId)
                return Forbid();
            return Ok(HierarchyMapping.ToDto(terminal));
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpPost]
        public async Task<ActionResult<TerminalDto>> CreateTerminal(TerminalDto dto)
        {
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && dto.BranchId != supBranchId)
                return Forbid();
            var error = await ValidateTerminal(dto, 0);
            if (error != null) return BadRequest(error);

            string generatedActivationCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            var terminal = new Terminal
            {
                CreatedAt = DateTime.UtcNow,
                Uuid = null,
                ActivationCode = generatedActivationCode,
                IsActivationCodeUsed = false,
                AllowSync = dto.AllowSync
            };
            ApplyTerminal(terminal, dto);
            terminal.Uuid = null;
            terminal.ActivationCode = generatedActivationCode;
            terminal.IsActivationCodeUsed = false;
            terminal.AllowSync = dto.AllowSync;

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
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && (terminal.BranchId != supBranchId || dto.BranchId != supBranchId))
                return Forbid();
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
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && terminal.BranchId != supBranchId)
                return Forbid();
            if (await HasHistory(id)) return Conflict("Terminal has historical records or invoice series. Deactivate it instead.");
            _context.Terminals.Remove(terminal);
            try { await _context.SaveChangesAsync(); }
            catch (DbUpdateException) { return Conflict("Terminal has related records and cannot be deleted."); }
            return NoContent();
        }

        [AllowAnonymous]
        [HttpPost("activate")]
        public async Task<ActionResult<TerminalActivationResponseDto>> ActivateTerminal([FromBody] TerminalActivationRequestDto request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ActivationCode))
                return BadRequest(new TerminalActivationResponseDto { Success = false, Message = "A valid 6-digit activation code is required." });

            string? clientUuid = !string.IsNullOrWhiteSpace(request.DeviceUuid)
                ? request.DeviceUuid.Trim()
                : request.DeviceIdentifier?.Trim();
            if (string.IsNullOrWhiteSpace(clientUuid))
                return BadRequest(new TerminalActivationResponseDto { Success = false, Message = "A client-generated Device UUID is required." });

            string code = request.ActivationCode.Trim();
            var terminal = await _context.Terminals
                .Include(t => t.Outlet)
                .Include(t => t.Branch)
                .SingleOrDefaultAsync(t => t.ActivationCode == code);

            if (terminal == null)
                return NotFound(new TerminalActivationResponseDto { Success = false, Message = "Invalid activation code. Please check the code and try again." });

            if (terminal.IsActivationCodeUsed)
                return Conflict(new TerminalActivationResponseDto { Success = false, Message = "This activation code has already been used." });

            if (!terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Branch.IsActive)
                return Conflict(new TerminalActivationResponseDto { Success = false, Message = "This terminal, outlet, or branch is inactive. Contact an administrator." });

            // Detach client UUID from any previous terminal before binding to this terminal
            var existingUuidOwners = await _context.Terminals
                .Where(t => t.TerminalId != terminal.TerminalId && t.Uuid == clientUuid)
                .ToListAsync();
            foreach (var old in existingUuidOwners)
            {
                old.Uuid = null;
            }

            // Bind client-generated UUID (overwriting any previous UUID on this terminal)
            terminal.Uuid = clientUuid;

            if (!string.IsNullOrWhiteSpace(request.DeviceIdentifier))
            {
                string devId = request.DeviceIdentifier.Trim().ToLowerInvariant();
                if (IPAddress.TryParse(devId, out var devIp)) devId = NormalizeIp(devIp);

                var duplicateDev = await _context.Terminals.AsNoTracking()
                    .AnyAsync(t => t.TerminalId != terminal.TerminalId && t.DeviceIdentifier == devId);
                if (duplicateDev)
                    return Conflict(new TerminalActivationResponseDto { Success = false, Message = "Device identifier is already registered to another terminal." });

                terminal.DeviceIdentifier = devId;
            }

            terminal.IsActivationCodeUsed = true;
            terminal.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            string? token = null;
            var jwtKey = _configuration["Jwt:Key"];
            if (!string.IsNullOrEmpty(jwtKey))
            {
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, terminal.TerminalId.ToString()),
                    new Claim(ClaimTypes.Name, terminal.TerminalCode),
                    new Claim(ClaimTypes.Role, "Terminal"),
                    new Claim("branch_id", terminal.BranchId.ToString()),
                    new Claim("terminal_id", terminal.TerminalId.ToString()),
                    new Claim("outlet_id", terminal.OutletId.ToString()),
                    new Claim("terminal_uuid", terminal.Uuid ?? string.Empty)
                };

                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
                var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

                var jwt = new JwtSecurityToken(
                    issuer: _configuration["Jwt:Issuer"],
                    audience: _configuration["Jwt:Audience"],
                    claims: claims,
                    expires: DateTime.UtcNow.AddYears(1),
                    signingCredentials: creds
                );

                token = new JwtSecurityTokenHandler().WriteToken(jwt);
            }

            var config = new TerminalConfigDto
            {
                TerminalId = terminal.TerminalId,
                MenuDisplayMode = terminal.MenuDisplayMode,
                OutletId = terminal.OutletId,
                OutletName = terminal.Outlet?.Name ?? string.Empty,
                TerminalCode = terminal.TerminalCode,
                DeviceIdentifier = terminal.DeviceIdentifier,
                IpAddress = terminal.IpAddress,
                PTUNumber = terminal.Ptunumber,
                MINNumber = terminal.Minnumber,
                SerialNumber = terminal.SerialNumber,
                BranchId = terminal.BranchId,
                BranchName = terminal.Branch?.Name ?? string.Empty,
                BranchCode = terminal.Branch?.Code ?? string.Empty,
                BranchAddress = terminal.Branch?.Address,
                BranchPhone = terminal.Branch?.Phone,
                TerminalName = terminal.Name,
                BusinessTypeId = terminal.Branch?.BusinessTypeId,
                BusinessType = terminal.Branch?.BusinessType?.Type,
                Token = token,
                Uuid = terminal.Uuid,
                AllowSync = terminal.AllowSync
            };

            var usersWithPins = await _context.Users
                .AsNoTracking()
                .Where(u => u.PinCode != null && u.PinCode != "")
                .ToListAsync();

            var cashiers = usersWithPins
                .Select(u => new OfflineCashierDto
                {
                    UserId = u.UserId,
                    Username = u.Username,
                    Role = u.Role,
                    PinHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(u.PinCode!.Trim())))
                })
                .ToList();

            return Ok(new TerminalActivationResponseDto
            {
                Success = true,
                Message = "Terminal successfully activated and paired.",
                Token = token,
                Terminal = HierarchyMapping.ToDto(terminal),
                Config = config,
                Cashiers = cashiers
            });
        }

        [AllowAnonymous]
        [HttpPost("session-token")]
        public async Task<ActionResult<TerminalSessionTokenResponseDto>> IssueSessionToken([FromBody] TerminalSessionTokenRequestDto request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.DeviceUuid))
                return BadRequest(new TerminalSessionTokenResponseDto { Success = false, Message = "DeviceUuid is required." });

            string uuid = request.DeviceUuid.Trim();
            var terminal = await _context.Terminals
                .AsNoTracking()
                .Include(t => t.Outlet)
                .Include(t => t.Branch)
                .FirstOrDefaultAsync(t =>
                    (request.TerminalId <= 0 || t.TerminalId == request.TerminalId) &&
                    (t.Uuid == uuid || t.DeviceIdentifier == uuid));

            if (terminal == null || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Branch.IsActive)
                return Unauthorized(new TerminalSessionTokenResponseDto { Success = false, Message = "Unpaired or inactive terminal." });

            var jwtKey = _configuration["Jwt:Key"];
            if (string.IsNullOrEmpty(jwtKey))
                return StatusCode(500, new TerminalSessionTokenResponseDto { Success = false, Message = "JWT key not configured." });

            User? user = null;
            if (!string.IsNullOrWhiteSpace(request.CashierUsername))
            {
                string uname = request.CashierUsername.Trim();
                user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Username == uname);
            }

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user?.UserId.ToString() ?? terminal.TerminalId.ToString()),
                new Claim(ClaimTypes.Name, user?.Username ?? (!string.IsNullOrWhiteSpace(request.CashierUsername) ? request.CashierUsername.Trim() : terminal.TerminalCode)),
                new Claim(ClaimTypes.Role, user?.Role ?? "Cashier"),
                new Claim("branch_id", terminal.BranchId.ToString()),
                new Claim("terminal_id", terminal.TerminalId.ToString()),
                new Claim("outlet_id", terminal.OutletId.ToString()),
                new Claim("terminal_uuid", terminal.Uuid ?? uuid)
            };

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var jwt = new JwtSecurityToken(
                issuer: _configuration["Jwt:Issuer"],
                audience: _configuration["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddDays(30),
                signingCredentials: creds
            );

            string token = new JwtSecurityTokenHandler().WriteToken(jwt);
            return Ok(new TerminalSessionTokenResponseDto { Success = true, Token = token });
        }

        [Authorize(Roles = "Admin,Manager")]
        [HttpPost("{id:int}/regenerate-activation-code")]
        public async Task<ActionResult<TerminalDto>> RegenerateActivationCode(int id)
        {
            var terminal = await _context.Terminals.Include(t => t.Outlet).Include(t => t.Branch).FirstOrDefaultAsync(t => t.TerminalId == id);
            if (terminal == null) return NotFound();
            if (User != null && !User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out var supBranchId) && terminal.BranchId != supBranchId)
                return Forbid();

            terminal.ActivationCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            terminal.IsActivationCodeUsed = false;
            terminal.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return Ok(HierarchyMapping.ToDto(terminal));
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
            if (!Enum.IsDefined(dto.MenuDisplayMode)) return "Unsupported menu display mode. Use TextOnly (0) or ImageAndText (1).";
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
            t.MenuDisplayMode = dto.MenuDisplayMode;
            t.Ptunumber = dto.PTUNumber; t.Minnumber = dto.MINNumber; t.SerialNumber = dto.SerialNumber;
            t.AllowSync = dto.AllowSync;
            if (string.IsNullOrWhiteSpace(t.Uuid))
            {
                t.Uuid = string.IsNullOrWhiteSpace(dto.Uuid) ? Guid.NewGuid().ToString() : dto.Uuid;
            }
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
