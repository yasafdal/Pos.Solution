using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using System.Security.Claims;

namespace POS.Api.Controllers
{
    [Route("api/cashier-sessions")]
    [ApiController]
    [Authorize]
    public class CashierSessionsController : ControllerBase
    {
        private readonly PosDbContext _context;

        public CashierSessionsController(PosDbContext context)
        {
            _context = context;
        }

        [HttpPost("activate")]
        public async Task<IActionResult> ActivateSession([FromBody] CashierSessionActivateDto request)
        {
            var cashierId = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(cashierId))
            {
                return Unauthorized("Invalid user identity.");
            }

            // Close any existing active sessions for this outlet/device
            var activeSessions = await _context.CashierSessions
                .Where(s => s.OutletId == request.OutletId && s.Status == "ACTIVE")
                .ToListAsync();

            foreach (var sess in activeSessions)
            {
                sess.Status = "SWITCHED_OUT";
                sess.LogoutAt = DateTime.UtcNow;
                sess.LogoutReason = "Handover / New session activation";
            }

            var newSession = new CashierSession
            {
                CashierId = cashierId,
                BranchId = request.BranchId,
                OutletId = request.OutletId,
                DeviceIdentifier = request.DeviceIdentifier,
                LoginAt = DateTime.UtcNow,
                Status = "ACTIVE",
                PreviousSessionId = activeSessions.FirstOrDefault()?.CashierSessionId
            };

            _context.CashierSessions.Add(newSession);

            if (activeSessions.Any())
            {
                foreach (var prev in activeSessions)
                {
                    _context.CashierSwitchLogs.Add(new CashierSwitchLog
                    {
                        OutletId = request.OutletId,
                        DeviceIdentifier = request.DeviceIdentifier,
                        PreviousCashierId = prev.CashierId,
                        NewCashierId = cashierId,
                        RequestedAt = DateTime.UtcNow,
                        CompletedAt = DateTime.UtcNow,
                        Status = "SUCCESS",
                        Reason = "Cashier Handover Switch"
                    });
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { Message = "Cashier session activated successfully.", SessionId = newSession.CashierSessionId });
        }

        [HttpGet("current")]
        public async Task<ActionResult<CashierSessionDto>> GetCurrentSession([FromQuery] int outletId)
        {
            var session = await _context.CashierSessions
                .AsNoTracking()
                .Where(s => s.OutletId == outletId && s.Status == "ACTIVE")
                .OrderByDescending(s => s.LoginAt)
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return NotFound("No active cashier session found for this outlet.");
            }

            return Ok(new CashierSessionDto
            {
                CashierSessionId = session.CashierSessionId,
                CashierId = session.CashierId,
                BranchId = session.BranchId,
                OutletId = session.OutletId,
                LoginAt = session.LoginAt,
                Status = session.Status
            });
        }

        [HttpPost("end")]
        public async Task<IActionResult> EndSession([FromBody] CashierSessionEndDto request)
        {
            var session = await _context.CashierSessions
                .Where(s => s.CashierSessionId == request.CashierSessionId && s.Status == "ACTIVE")
                .FirstOrDefaultAsync();

            if (session == null)
            {
                return NotFound("Active session not found.");
            }

            session.Status = "LOGGED_OUT";
            session.LogoutAt = DateTime.UtcNow;
            session.LogoutReason = request.Reason ?? "Manual logout / End of shift";

            await _context.SaveChangesAsync();
            return Ok(new { Message = "Cashier session ended successfully." });
        }
    }
}
