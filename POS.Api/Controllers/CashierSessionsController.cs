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
    [Authorize(Policy = POS.Shared.Constants.PosPermissions.ClientOperationsPolicy)]
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

            var terminal = await _context.Terminals.AsNoTracking().Include(t => t.Outlet).ThenInclude(o => o.Branch)
                .SingleOrDefaultAsync(t => t.TerminalId == request.TerminalId);
            if (terminal == null || terminal.BranchId != request.BranchId || !terminal.IsActive || !terminal.Outlet.IsActive || !terminal.Outlet.Branch.IsActive)
                return BadRequest("Invalid or inactive terminal ownership.");
            if (User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString() && !User.IsInRole("Admin") && !User.IsInRole("Manager")) return Forbid();

            // Serialize concurrent login/activation attempts on the same terminal to prevent ownership races
            await using var tx = await _context.Database.BeginTransactionAsync(
                _context.Database.IsNpgsql() ? System.Data.IsolationLevel.ReadCommitted : System.Data.IsolationLevel.Serializable);
            await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(_context, request.TerminalId);

            var lockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(_context, request.TerminalId, cashierId);
            if (lockState.IsCashierRestricted)
            {
                await tx.RollbackAsync();
                return StatusCode(403, lockState.CashierRestrictionMessage ?? lockState.LockMessage);
            }
            if (lockState.IsTerminalLocked)
            {
                await tx.RollbackAsync();
                return StatusCode(423, lockState.LockMessage ?? "POS Terminal is locked for the current business day.");
            }
            if (lockState.IsShiftOwnedByAnotherCashier)
            {
                await tx.RollbackAsync();
                return StatusCode(409, lockState.ActiveShiftOwnershipMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
            }

            var activeSessions = await _context.CashierSessions
                .Where(s => s.TerminalId == request.TerminalId && s.Status == "ACTIVE")
                .OrderByDescending(s => s.LoginAt)
                .ToListAsync();

            // Same cashier re-login: resume existing ACTIVE session without creating duplicates or bogus handover logs
            var existingOwnSession = activeSessions.FirstOrDefault(s =>
                string.Equals(s.CashierId, cashierId, StringComparison.OrdinalIgnoreCase));
            if (existingOwnSession != null)
            {
                if (!string.IsNullOrWhiteSpace(request.DeviceIdentifier))
                    existingOwnSession.DeviceIdentifier = request.DeviceIdentifier;

                // Clean up any stray non-owner idle sessions (safe because we already proved no other cashier owns an open shift)
                foreach (var stray in activeSessions.Where(s => s.CashierSessionId != existingOwnSession.CashierSessionId))
                {
                    stray.Status = "SWITCHED_OUT";
                    stray.LogoutAt = POS.Shared.Services.BusinessDayClock.UtcNow();
                    stray.LogoutReason = "Resumed owner session";
                }

                await _context.SaveChangesAsync();
                await tx.CommitAsync();
                return Ok(new { Message = "Existing cashier session resumed.", SessionId = existingOwnSession.CashierSessionId, Resumed = true });
            }

            var nowUtc = POS.Shared.Services.BusinessDayClock.UtcNow();
            foreach (var sess in activeSessions)
            {
                sess.Status = "SWITCHED_OUT";
                sess.LogoutAt = nowUtc;
                sess.LogoutReason = "Handover / New session activation";
            }

            var newSession = new CashierSession
            {
                CashierId = cashierId,
                BranchId = request.BranchId,
                TerminalId = request.TerminalId,
                DeviceIdentifier = request.DeviceIdentifier,
                LoginAt = nowUtc,
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
                        TerminalId = request.TerminalId,
                        DeviceIdentifier = request.DeviceIdentifier,
                        PreviousCashierId = prev.CashierId,
                        NewCashierId = cashierId,
                        RequestedAt = nowUtc,
                        CompletedAt = nowUtc,
                        Status = "SUCCESS",
                        Reason = "Cashier Handover Switch"
                    });
                }
            }

            await _context.SaveChangesAsync();
            await tx.CommitAsync();
            return Ok(new { Message = "Cashier session activated successfully.", SessionId = newSession.CashierSessionId, Resumed = false });
        }

        [HttpGet("current")]
        public async Task<ActionResult<CashierSessionDto>> GetCurrentSession([FromQuery] int terminalId)
        {
            if (!User.IsInRole("Admin") && !User.IsInRole("Manager") && User.FindFirst("terminal_id")?.Value != terminalId.ToString())
                return Forbid();

            var session = await _context.CashierSessions
                .AsNoTracking()
                .Where(s => s.TerminalId == terminalId && s.Status == "ACTIVE")
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
                TerminalId = session.TerminalId,
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

            var cashierId = User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!User.IsInRole("Admin") && !User.IsInRole("Manager") && session.CashierId != cashierId && User.FindFirst("terminal_id")?.Value != session.TerminalId.ToString())
                return Forbid();

            session.Status = "LOGGED_OUT";
            session.LogoutAt = DateTime.UtcNow;
            session.LogoutReason = request.Reason ?? "Manual logout / End of shift";

            await _context.SaveChangesAsync();
            return Ok(new { Message = "Cashier session ended successfully." });
        }
    }
}
