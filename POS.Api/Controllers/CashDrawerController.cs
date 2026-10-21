using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace POS.Api.Controllers;

[Route("api/cash-drawer")]
[ApiController]
[Authorize]
public class CashDrawerController : ControllerBase
{
    private readonly PosDbContext _context;

    public CashDrawerController(PosDbContext context)
    {
        _context = context;
    }

    private string? GetAuthenticatedUsername() =>
        User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private string GetAuthenticatedRole() =>
        User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    private static bool IsSupervisorRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var clean = role.Trim();
        return clean.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<User?> VerifySupervisorPinAsync(string? rawPin, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawPin)) return null;
        var cleanPin = rawPin.Trim();
        // SECURITY: Strictly match PinCode only (never match Username as PIN)
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.PinCode != null && u.PinCode == cleanPin, cancellationToken);

        if (user == null || !IsSupervisorRole(user.Role))
            return null;

        return user;
    }

    public static void RecalculateShiftExpectedBalance(CashDrawerShift shift)
    {
        shift.ExpectedBalance = Math.Round(
            shift.OpeningFloat
            + shift.TotalCashSales
            - shift.TotalCashRefunds
            + shift.TotalCashIn
            - shift.TotalCashOut
            - shift.TotalCashDrops,
            2);
    }

    private static string GetVoucherPrefix(string movementType) =>
        (movementType ?? string.Empty).ToUpperInvariant() switch
        {
            "PETTY_CASH" => "PCV",
            "CASH_OUT" => "COU",
            "CASH_IN" => "CIN",
            "CASH_DROP" => "CDP",
            "REVERSAL" => "REV",
            _ => "CMV"
        };

    private static string NormalizeMovementType(string? movementType)
    {
        var upper = (movementType ?? "PETTY_CASH").Trim().ToUpperInvariant();
        return upper switch
        {
            "CASH_IN" or "PAID_IN" => "CASH_IN",
            "CASH_OUT" or "PAID_OUT" => "CASH_OUT",
            "PETTY_CASH" => "PETTY_CASH",
            "CASH_DROP" or "DROP" => "CASH_DROP",
            _ => upper
        };
    }

    private static bool IsOutflowMovement(string movementType) =>
        movementType is "CASH_OUT" or "PETTY_CASH" or "CASH_DROP";

    private async Task EnsureDefaultCategoriesSeededAsync(CancellationToken cancellationToken = default)
    {
        if (await _context.CashMovementCategories.AnyAsync(cancellationToken))
            return;

        var defaults = new List<CashMovementCategory>
        {
            new() { Name = "Store Supplies / Ice / Packaging", Description = "Emergency F&B store consumables and packaging paid from drawer", MovementType = "PETTY_CASH", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true },
            new() { Name = "Delivery / Rider / Transport", Description = "Rider fee, fuel, or urgent local delivery expense", MovementType = "PETTY_CASH", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true },
            new() { Name = "Utilities / Minor Repair", Description = "Urgent store maintenance or utility expense", MovementType = "PETTY_CASH", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true },
            new() { Name = "Supplier COD / Emergency Ingredient", Description = "Cash on delivery for urgent kitchen raw materials", MovementType = "CASH_OUT", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true },
            new() { Name = "Safe Cash Drop / Remittance", Description = "Mid-shift excess cash transfer to store safe", MovementType = "CASH_DROP", RequiresApproval = true, ApprovalThreshold = 0m, IsActive = true },
            new() { Name = "Additional Change Fund (Paid-In)", Description = "Bills broken into coins or additional float added to drawer", MovementType = "CASH_IN", RequiresApproval = false, ApprovalThreshold = 1000m, IsActive = true },
            new() { Name = "Petty Cash Replenishment / Return", Description = "Unused petty cash returned to cash drawer", MovementType = "CASH_IN", RequiresApproval = false, ApprovalThreshold = 0m, IsActive = true }
        };

        _context.CashMovementCategories.AddRange(defaults);
        await _context.SaveChangesAsync(cancellationToken);
    }

    [HttpGet("categories")]
    public async Task<ActionResult<List<CashMovementCategoryDto>>> GetCategories(
        [FromQuery] string? movementType = null,
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await EnsureDefaultCategoriesSeededAsync(cancellationToken);

        var query = _context.CashMovementCategories.AsNoTracking().AsQueryable();
        if (!includeInactive)
            query = query.Where(c => c.IsActive);

        if (!string.IsNullOrWhiteSpace(movementType))
        {
            var normalized = NormalizeMovementType(movementType);
            query = query.Where(c => c.MovementType == normalized);
        }

        var list = await query
            .OrderBy(c => c.MovementType)
            .ThenBy(c => c.Name)
            .Select(c => new CashMovementCategoryDto
            {
                CategoryId = c.CategoryId,
                Name = c.Name,
                Description = c.Description,
                MovementType = c.MovementType,
                RequiresApproval = c.RequiresApproval,
                ApprovalThreshold = c.ApprovalThreshold,
                IsActive = c.IsActive
            })
            .ToListAsync(cancellationToken);

        return Ok(list);
    }

    [HttpPost("categories")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CashMovementCategoryDto>> CreateCategory(
        [FromBody] CashMovementCategoryDto dto,
        CancellationToken cancellationToken = default)
    {
        if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Category name is required.");

        var normalizedType = NormalizeMovementType(dto.MovementType);
        if (normalizedType is not ("PETTY_CASH" or "CASH_OUT" or "CASH_IN" or "CASH_DROP"))
            return BadRequest("Invalid movement type. Must be PETTY_CASH, CASH_OUT, CASH_IN, or CASH_DROP.");

        var entity = new CashMovementCategory
        {
            Name = dto.Name.Trim(),
            Description = dto.Description?.Trim(),
            MovementType = normalizedType,
            RequiresApproval = dto.RequiresApproval,
            ApprovalThreshold = Math.Max(0m, Math.Round(dto.ApprovalThreshold, 2)),
            IsActive = dto.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        _context.CashMovementCategories.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        dto.CategoryId = entity.CategoryId;
        dto.MovementType = entity.MovementType;
        return Ok(dto);
    }

    [HttpPut("categories/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CashMovementCategoryDto>> UpdateCategory(
        int id,
        [FromBody] CashMovementCategoryDto dto,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.CashMovementCategories.FindAsync(new object[] { id }, cancellationToken);
        if (entity == null) return NotFound("Category not found.");

        if (string.IsNullOrWhiteSpace(dto.Name))
            return BadRequest("Category name is required.");

        var normalizedType = NormalizeMovementType(dto.MovementType);
        if (normalizedType is not ("PETTY_CASH" or "CASH_OUT" or "CASH_IN" or "CASH_DROP"))
            return BadRequest("Invalid movement type.");

        entity.Name = dto.Name.Trim();
        entity.Description = dto.Description?.Trim();
        entity.MovementType = normalizedType;
        entity.RequiresApproval = dto.RequiresApproval;
        entity.ApprovalThreshold = Math.Max(0m, Math.Round(dto.ApprovalThreshold, 2));
        entity.IsActive = dto.IsActive;

        await _context.SaveChangesAsync(cancellationToken);
        dto.CategoryId = entity.CategoryId;
        dto.MovementType = entity.MovementType;
        return Ok(dto);
    }

    [HttpDelete("categories/{id:int}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeactivateCategory(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.CashMovementCategories.FindAsync(new object[] { id }, cancellationToken);
        if (entity == null) return NotFound("Category not found.");

        entity.IsActive = false;
        await _context.SaveChangesAsync(cancellationToken);
        return Ok(new { Message = "Category deactivated successfully." });
    }

    [HttpPost("open-shift")]
    public async Task<IActionResult> OpenShift([FromBody] OpenShiftDto request, CancellationToken cancellationToken = default)
    {
        var cashierId = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(cashierId)) return Unauthorized();

        if (request.OpeningFloat < 0)
            return BadRequest("Opening float cannot be negative.");

        if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString()) return Forbid();
        var outlet = await _context.Terminals.AsNoTracking()
            .FirstOrDefaultAsync(o => o.TerminalId == request.TerminalId, cancellationToken);
        if (outlet == null || !outlet.IsActive || !await _context.Outlets.AnyAsync(o => o.OutletId == outlet.OutletId && o.IsActive && o.Branch.IsActive, cancellationToken))
            return BadRequest("Select a valid active terminal and outlet.");
        if (outlet != null && request.BranchId <= 0)
        {
            request.BranchId = outlet.BranchId;
        }
        else if (outlet != null && outlet.BranchId != request.BranchId)
        {
            return BadRequest("Terminal does not belong to the specified branch.");
        }

        var existingOpen = await _context.CashDrawerShifts
            .FirstOrDefaultAsync(s => s.TerminalId == request.TerminalId && s.Status == "OPEN", cancellationToken);

        if (existingOpen != null)
        {
            return BadRequest("An open shift already exists for this outlet.");
        }

        decimal openingFloat = Math.Round(request.OpeningFloat, 2);
        var shift = new CashDrawerShift
        {
            BranchId = request.BranchId > 0 ? request.BranchId : (outlet?.BranchId ?? 1),
            TerminalId = request.TerminalId,
            CashierId = cashierId,
            OpeningFloat = openingFloat,
            ExpectedBalance = openingFloat,
            Status = "OPEN",
            OpenedAt = DateTime.UtcNow,
            Notes = request.Notes
        };

        _context.CashDrawerShifts.Add(shift);
        await _context.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Cash drawer shift opened successfully.", ShiftId = shift.ShiftId });
    }

    [HttpGet("current")]
    public async Task<ActionResult<CashDrawerShiftDto>> GetCurrentShift([FromQuery] int terminalId, CancellationToken cancellationToken = default)
    {
        var shift = await _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (shift == null) return NotFound("No active open shift found for this outlet.");

        return Ok(MapShiftToDto(shift));
    }

    [HttpGet("shifts")]
    public async Task<ActionResult<List<CashDrawerShiftDto>>> GetShifts(
        [FromQuery] int? branchId = null,
        [FromQuery] int? terminalId = null,
        [FromQuery] string? cashierId = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .AsQueryable();

        if (branchId.HasValue) query = query.Where(s => s.BranchId == branchId.Value);
        if (terminalId.HasValue) query = query.Where(s => s.TerminalId == terminalId.Value);
        if (!string.IsNullOrWhiteSpace(cashierId)) query = query.Where(s => s.CashierId == cashierId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(s => s.Status == status);
        if (dateFrom.HasValue) query = query.Where(s => s.OpenedAt >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(s => s.OpenedAt <= dateTo.Value);

        var shifts = await query
            .OrderByDescending(s => s.OpenedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        return Ok(shifts.Select(MapShiftToDto).ToList());
    }

    [HttpGet("shifts/{shiftId:long}/totals")]
    public async Task<ActionResult<ShiftCashMovementSummaryDto>> GetShiftTotals(long shiftId, CancellationToken cancellationToken = default)
    {
        var shift = await _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .FirstOrDefaultAsync(s => s.ShiftId == shiftId, cancellationToken);

        if (shift == null) return NotFound("Shift not found.");

        var postedMovements = shift.Movements.Where(m => (m.Status ?? "POSTED") == "POSTED").ToList();
        decimal pettyCashOut = postedMovements.Where(m => m.MovementType == "PETTY_CASH").Sum(m => m.Amount);

        return Ok(new ShiftCashMovementSummaryDto
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            TerminalId = shift.TerminalId,
            CashierId = shift.CashierId,
            OpeningCash = shift.OpeningFloat,
            CashSales = shift.TotalCashSales,
            CashPaidIn = shift.TotalCashIn,
            CashPaidOut = shift.TotalCashOut,
            PettyCashOut = pettyCashOut,
            CashDrops = shift.TotalCashDrops,
            CashRefunds = shift.TotalCashRefunds,
            ExpectedCash = shift.ExpectedBalance,
            ActualCountedCash = shift.ActualCountedBalance,
            OverShortAmount = shift.Discrepancy,
            PostedMovementCount = postedMovements.Count(m => m.MovementType is "CASH_IN" or "CASH_OUT" or "PETTY_CASH" or "CASH_DROP"),
            PendingApprovalCount = shift.Movements.Count(m => m.Status == "PENDING_APPROVAL"),
            RejectedMovementCount = shift.Movements.Count(m => m.Status == "REJECTED"),
            ReversedMovementCount = shift.Movements.Count(m => m.Status == "REVERSED")
        });
    }

    [HttpPost("paid-in")]
    public Task<ActionResult<CashMovementResponseDto>> CreatePaidIn([FromBody] CashDrawerMovementDto request, CancellationToken cancellationToken = default)
    {
        if (request != null) request.MovementType = "CASH_IN";
        return ProcessMovementInternalAsync(request, cancellationToken);
    }

    [HttpPost("paid-out")]
    public Task<ActionResult<CashMovementResponseDto>> CreatePaidOut([FromBody] CashDrawerMovementDto request, CancellationToken cancellationToken = default)
    {
        if (request != null && string.IsNullOrWhiteSpace(request.MovementType))
            request.MovementType = "PETTY_CASH";
        return ProcessMovementInternalAsync(request, cancellationToken);
    }

    [HttpPost("movement")]
    public Task<ActionResult<CashMovementResponseDto>> RecordMovement([FromBody] CashDrawerMovementDto request, CancellationToken cancellationToken = default)
    {
        return ProcessMovementInternalAsync(request, cancellationToken);
    }

    private async Task<ActionResult<CashMovementResponseDto>> ProcessMovementInternalAsync(
        CashDrawerMovementDto? request,
        CancellationToken cancellationToken)
    {
        var cashierId = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(cashierId)) return Unauthorized();

        if (request == null) return BadRequest("Invalid cash movement request.");
        decimal amount = Math.Round(request.Amount, 2);
        if (amount <= 0) return BadRequest("Amount must be greater than zero.");
        if (string.IsNullOrWhiteSpace(request.Reason)) return BadRequest("Reason is required for cash movements.");

        var movementType = NormalizeMovementType(request.MovementType);
        if (movementType is not ("CASH_IN" or "CASH_OUT" or "PETTY_CASH" or "CASH_DROP"))
        {
            return BadRequest("Unsupported movement type. Must be CASH_IN, CASH_OUT, PETTY_CASH, or CASH_DROP.");
        }

        // Idempotency check via ClientMovementId
        if (!string.IsNullOrWhiteSpace(request.ClientMovementId))
        {
            var existingByClient = await _context.CashDrawerMovements
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ClientMovementId == request.ClientMovementId.Trim(), cancellationToken);
            if (existingByClient != null)
            {
                return Ok(MapMovementToDto(existingByClient));
            }
        }

        using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
            {
                shift = await _context.CashDrawerShifts
                    .FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId, cancellationToken);
            }
            else if (request.TerminalId > 0)
            {
                shift = await _context.CashDrawerShifts
                    .Where(s => s.TerminalId == request.TerminalId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (shift == null || shift.Status != "OPEN")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Cash movements require an active open cashier shift.");
            }

            if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != shift.TerminalId.ToString()) return Forbid();
            // Validate branch & outlet ownership
            if (request.TerminalId > 0 && shift.TerminalId != request.TerminalId)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Shift does not belong to the specified outlet.");
            }
            if (request.BranchId > 0 && shift.BranchId != request.BranchId)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Shift does not belong to the specified branch.");
            }

            var callerRole = GetAuthenticatedRole();
            if (!IsSupervisorRole(callerRole) && !string.Equals(shift.CashierId, cashierId, StringComparison.OrdinalIgnoreCase))
            {
                // Also check if cashier is the currently active session on this outlet
                var activeSession = await _context.CashierSessions
                    .AsNoTracking()
                    .Where(s => s.TerminalId == shift.TerminalId && s.Status == "ACTIVE")
                    .OrderByDescending(s => s.LoginAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (activeSession == null || !string.Equals(activeSession.CashierId, cashierId, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return StatusCode(403, "Unauthorized: Cashier does not own the active shift or terminal session for this outlet.");
                }
            }

            // Resolve category & approval requirement
            CashMovementCategory? category = null;
            if (request.CategoryId.HasValue && request.CategoryId.Value > 0)
            {
                category = await _context.CashMovementCategories
                    .AsNoTracking()
                    .FirstOrDefaultAsync(c => c.CategoryId == request.CategoryId.Value && c.IsActive, cancellationToken);
                if (category == null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return BadRequest("Selected cash movement category is invalid or inactive.");
                }
            }

            bool requiresApproval = IsOutflowMovement(movementType);
            if (category != null)
            {
                requiresApproval = category.RequiresApproval ||
                                   (category.ApprovalThreshold > 0m && amount >= category.ApprovalThreshold);
            }

            string? approvedBy = null;
            DateTime? approvedAt = null;
            string status = "POSTED";

            if (requiresApproval)
            {
                if (!string.IsNullOrWhiteSpace(request.SupervisorPin))
                {
                    var supervisor = await VerifySupervisorPinAsync(request.SupervisorPin, cancellationToken);
                    if (supervisor == null)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return BadRequest("Invalid supervisor PIN or user does not have supervisor approval privileges.");
                    }
                    approvedBy = supervisor.Username;
                    approvedAt = DateTime.UtcNow;
                    status = "POSTED";
                }
                else if (IsSupervisorRole(callerRole))
                {
                    approvedBy = cashierId;
                    approvedAt = DateTime.UtcNow;
                    status = "POSTED";
                }
                else
                {
                    status = "PENDING_APPROVAL";
                }
            }
            else
            {
                approvedBy = cashierId;
                approvedAt = DateTime.UtcNow;
                status = "POSTED";
            }

            // Enforce available cash balance check for all outflows (even when creating a pending request, prevent obviously over-balance requests)
            RecalculateShiftExpectedBalance(shift);
            if (IsOutflowMovement(movementType) && amount > shift.ExpectedBalance)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Insufficient cash in drawer. Available expected cash is ₱{shift.ExpectedBalance:N2}, requested paid-out is ₱{amount:N2}.");
            }

            var now = DateTime.UtcNow;
            var movement = new CashDrawerMovement
            {
                ShiftId = shift.ShiftId,
                BranchId = shift.BranchId,
                TerminalId = shift.TerminalId,
                MovementType = movementType,
                CategoryId = category?.CategoryId ?? request.CategoryId,
                CategoryName = category?.Name ?? request.CategoryName,
                Amount = amount,
                Reason = request.Reason.Trim(),
                ClientMovementId = string.IsNullOrWhiteSpace(request.ClientMovementId) ? null : request.ClientMovementId.Trim(),
                Status = status,
                CreatedBy = cashierId,
                AuthorizedBy = approvedBy ?? cashierId,
                ApprovedBy = approvedBy,
                CreatedAt = now,
                ApprovedAt = approvedAt,
                PostedAt = status == "POSTED" ? now : null
            };

            _context.CashDrawerMovements.Add(movement);
            await _context.SaveChangesAsync(cancellationToken);

            // Generate deterministic internal voucher reference number (never touching BIR sales InvoiceSequences)
            string prefix = GetVoucherPrefix(movementType);
            movement.ReferenceNumber = $"{prefix}-{now:yyMMdd}-{movement.MovementId:D4}";

            // Only POSTED movements affect shift drawer balances
            if (status == "POSTED")
            {
                ApplyMovementToShiftTotals(shift, movementType, amount, isReversal: false);
            }

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(MapMovementToDto(movement));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to record cash movement: {ex.Message}");
        }
    }

    public static void ApplyMovementToShiftTotals(CashDrawerShift shift, string movementType, decimal amount, bool isReversal)
    {
        decimal delta = isReversal ? -amount : amount;
        switch (movementType)
        {
            case "CASH_IN":
                shift.TotalCashIn = Math.Max(0m, shift.TotalCashIn + delta);
                break;
            case "CASH_OUT":
            case "PETTY_CASH":
                shift.TotalCashOut = Math.Max(0m, shift.TotalCashOut + delta);
                break;
            case "CASH_DROP":
                shift.TotalCashDrops = Math.Max(0m, shift.TotalCashDrops + delta);
                break;
        }
        RecalculateShiftExpectedBalance(shift);
    }

    [HttpGet("movements/{movementId:long}")]
    public async Task<ActionResult<CashMovementResponseDto>> GetMovementById(long movementId, CancellationToken cancellationToken = default)
    {
        var movement = await _context.CashDrawerMovements
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.MovementId == movementId, cancellationToken);

        if (movement == null) return NotFound("Cash movement not found.");
        return Ok(MapMovementToDto(movement));
    }

    [HttpGet("movements")]
    public async Task<ActionResult<List<CashMovementResponseDto>>> GetMovements(
        [FromQuery] long? shiftId = null,
        [FromQuery] int? branchId = null,
        [FromQuery] int? terminalId = null,
        [FromQuery] string? cashierId = null,
        [FromQuery] string? movementType = null,
        [FromQuery] string? status = null,
        [FromQuery] int? categoryId = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.CashDrawerMovements
            .AsNoTracking()
            .Where(m => m.MovementType != "CASH_SALE" && m.MovementType != "CASH_REFUND")
            .AsQueryable();

        if (shiftId.HasValue) query = query.Where(m => m.ShiftId == shiftId.Value);
        if (branchId.HasValue) query = query.Where(m => m.BranchId == branchId.Value);
        if (terminalId.HasValue) query = query.Where(m => m.TerminalId == terminalId.Value);
        if (!string.IsNullOrWhiteSpace(cashierId))
            query = query.Where(m => m.CreatedBy == cashierId || m.AuthorizedBy == cashierId);
        if (!string.IsNullOrWhiteSpace(movementType))
            query = query.Where(m => m.MovementType == NormalizeMovementType(movementType));
        if (!string.IsNullOrWhiteSpace(status))
            query = query.Where(m => m.Status == status.Trim().ToUpperInvariant());
        if (categoryId.HasValue)
            query = query.Where(m => m.CategoryId == categoryId.Value);
        if (dateFrom.HasValue)
            query = query.Where(m => m.CreatedAt >= dateFrom.Value);
        if (dateTo.HasValue)
            query = query.Where(m => m.CreatedAt <= dateTo.Value);

        var items = await query
            .OrderByDescending(m => m.CreatedAt)
            .Take(500)
            .ToListAsync(cancellationToken);

        return Ok(items.Select(MapMovementToDto).ToList());
    }

    [HttpPost("movements/{movementId:long}/approve")]
    public async Task<ActionResult<CashMovementResponseDto>> ApproveMovement(
        long movementId,
        [FromBody] CashMovementApproveRejectDto? dto,
        CancellationToken cancellationToken = default)
    {
        var caller = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(caller)) return Unauthorized();

        string? approverUsername = null;
        if (!string.IsNullOrWhiteSpace(dto?.SupervisorPin))
        {
            var supervisor = await VerifySupervisorPinAsync(dto.SupervisorPin, cancellationToken);
            if (supervisor == null)
                return BadRequest("Invalid supervisor PIN or insufficient role for approval.");
            approverUsername = supervisor.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            approverUsername = caller;
        }
        else
        {
            return StatusCode(403, "Supervisor approval requires a Manager/Admin/Senior Cashier role or valid supervisor PIN.");
        }

        using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var movement = await _context.CashDrawerMovements
                .Include(m => m.CashDrawerShift)
                .FirstOrDefaultAsync(m => m.MovementId == movementId, cancellationToken);

            if (movement == null) return NotFound("Cash movement not found.");
            if (movement.Status == "POSTED")
            {
                await tx.RollbackAsync(cancellationToken);
                return Ok(MapMovementToDto(movement)); // Idempotent replay
            }
            if (movement.Status != "PENDING_APPROVAL")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Cannot approve movement in '{movement.Status}' status.");
            }

            var shift = movement.CashDrawerShift;
            if (shift == null || shift.Status != "OPEN")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Cannot approve movement because its cash drawer shift is already closed.");
            }

            RecalculateShiftExpectedBalance(shift);
            if (IsOutflowMovement(movement.MovementType) && movement.Amount > shift.ExpectedBalance)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Insufficient cash in drawer to post movement. Available expected cash is ₱{shift.ExpectedBalance:N2}, movement amount is ₱{movement.Amount:N2}.");
            }

            var now = DateTime.UtcNow;
            movement.Status = "POSTED";
            movement.ApprovedBy = approverUsername;
            movement.AuthorizedBy = approverUsername;
            movement.ApprovedAt = now;
            movement.PostedAt = now;

            ApplyMovementToShiftTotals(shift, movement.MovementType, movement.Amount, isReversal: false);

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(MapMovementToDto(movement));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to approve movement: {ex.Message}");
        }
    }

    [HttpPost("movements/{movementId:long}/reject")]
    public async Task<ActionResult<CashMovementResponseDto>> RejectMovement(
        long movementId,
        [FromBody] CashMovementApproveRejectDto? dto,
        CancellationToken cancellationToken = default)
    {
        var caller = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(caller)) return Unauthorized();

        string? approverUsername = null;
        if (!string.IsNullOrWhiteSpace(dto?.SupervisorPin))
        {
            var supervisor = await VerifySupervisorPinAsync(dto.SupervisorPin, cancellationToken);
            if (supervisor == null)
                return BadRequest("Invalid supervisor PIN or insufficient role to reject movement.");
            approverUsername = supervisor.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            approverUsername = caller;
        }
        else
        {
            return StatusCode(403, "Rejecting a cash movement requires a supervisor role or valid supervisor PIN.");
        }

        using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var movement = await _context.CashDrawerMovements
                .FirstOrDefaultAsync(m => m.MovementId == movementId, cancellationToken);

            if (movement == null) return NotFound("Cash movement not found.");
            if (movement.Status == "REJECTED")
            {
                await tx.RollbackAsync(cancellationToken);
                return Ok(MapMovementToDto(movement));
            }
            if (movement.Status != "PENDING_APPROVAL")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Only PENDING_APPROVAL movements can be rejected (current status: {movement.Status}).");
            }

            movement.Status = "REJECTED";
            movement.ApprovedBy = approverUsername;
            movement.ApprovedAt = DateTime.UtcNow;
            movement.RejectionReason = string.IsNullOrWhiteSpace(dto?.Reason) ? "Rejected by supervisor" : dto!.Reason!.Trim();

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(MapMovementToDto(movement));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to reject movement: {ex.Message}");
        }
    }

    [HttpPost("movements/{movementId:long}/reverse")]
    public async Task<ActionResult<CashMovementResponseDto>> ReverseMovement(
        long movementId,
        [FromBody] CashMovementReverseDto dto,
        CancellationToken cancellationToken = default)
    {
        var caller = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(caller)) return Unauthorized();
        if (dto == null || string.IsNullOrWhiteSpace(dto.Reason))
            return BadRequest("A reversal reason is required.");

        string? supervisorUsername = null;
        if (!string.IsNullOrWhiteSpace(dto.SupervisorPin))
        {
            var supervisor = await VerifySupervisorPinAsync(dto.SupervisorPin, cancellationToken);
            if (supervisor == null)
                return BadRequest("Invalid supervisor PIN or insufficient role for reversal.");
            supervisorUsername = supervisor.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            supervisorUsername = caller;
        }
        else
        {
            return StatusCode(403, "Reversing a posted cash movement requires a supervisor role or valid supervisor PIN.");
        }

        using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var movement = await _context.CashDrawerMovements
                .Include(m => m.CashDrawerShift)
                .FirstOrDefaultAsync(m => m.MovementId == movementId, cancellationToken);

            if (movement == null) return NotFound("Cash movement not found.");
            if (movement.Status == "REVERSED")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Cash movement has already been reversed.");
            }
            if (movement.Status != "POSTED" || movement.MovementType == "REVERSAL")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Only POSTED non-reversal cash movements can be reversed.");
            }

            var shift = movement.CashDrawerShift;
            if (shift == null || shift.Status != "OPEN")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Cannot reverse movement because its cash drawer shift is already closed.");
            }

            RecalculateShiftExpectedBalance(shift);
            if (movement.MovementType == "CASH_IN" && shift.ExpectedBalance < movement.Amount)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Cannot reverse Paid-In of ₱{movement.Amount:N2} because current expected drawer balance is only ₱{shift.ExpectedBalance:N2}.");
            }

            var now = DateTime.UtcNow;
            movement.Status = "REVERSED";
            movement.ReversedBy = supervisorUsername;
            movement.ReversedAt = now;
            movement.ReversalReason = dto.Reason.Trim();

            var reversalEntry = new CashDrawerMovement
            {
                ShiftId = shift.ShiftId,
                BranchId = shift.BranchId,
                TerminalId = shift.TerminalId,
                MovementType = "REVERSAL",
                CategoryId = movement.CategoryId,
                CategoryName = movement.CategoryName,
                Amount = movement.Amount,
                Reason = $"Reversal of {movement.ReferenceNumber}: {dto.Reason.Trim()}",
                Status = "POSTED",
                CreatedBy = caller,
                AuthorizedBy = supervisorUsername,
                ApprovedBy = supervisorUsername,
                CreatedAt = now,
                ApprovedAt = now,
                PostedAt = now,
                OriginalMovementId = movement.MovementId
            };

            _context.CashDrawerMovements.Add(reversalEntry);
            await _context.SaveChangesAsync(cancellationToken);

            reversalEntry.ReferenceNumber = $"REV-{now:yyMMdd}-{reversalEntry.MovementId:D4}";
            ApplyMovementToShiftTotals(shift, movement.MovementType, movement.Amount, isReversal: true);

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(MapMovementToDto(movement));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to reverse movement: {ex.Message}");
        }
    }

    [HttpGet("reports/summary")]
    public async Task<ActionResult<CashMovementSummaryReportDto>> GetMovementSummaryReport(
        [FromQuery] int? branchId = null,
        [FromQuery] int? terminalId = null,
        [FromQuery] string? cashierId = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default)
    {
        var query = _context.CashDrawerMovements
            .AsNoTracking()
            .Where(m => m.MovementType != "CASH_SALE" && m.MovementType != "CASH_REFUND")
            .AsQueryable();

        if (branchId.HasValue) query = query.Where(m => m.BranchId == branchId.Value);
        if (terminalId.HasValue) query = query.Where(m => m.TerminalId == terminalId.Value);
        if (!string.IsNullOrWhiteSpace(cashierId))
            query = query.Where(m => m.CreatedBy == cashierId || m.AuthorizedBy == cashierId);
        if (dateFrom.HasValue) query = query.Where(m => m.CreatedAt >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(m => m.CreatedAt <= dateTo.Value);

        var movements = await query.ToListAsync(cancellationToken);
        var posted = movements.Where(m => (m.Status ?? "POSTED") == "POSTED" && m.MovementType != "REVERSAL").ToList();

        var report = new CashMovementSummaryReportDto
        {
            TotalPaidIn = posted.Where(m => m.MovementType == "CASH_IN").Sum(m => m.Amount),
            TotalPaidOut = posted.Where(m => m.MovementType is "CASH_OUT" or "PETTY_CASH").Sum(m => m.Amount),
            TotalPettyCash = posted.Where(m => m.MovementType == "PETTY_CASH").Sum(m => m.Amount),
            TotalCashDrops = posted.Where(m => m.MovementType == "CASH_DROP").Sum(m => m.Amount),
            TotalReversed = movements.Where(m => m.Status == "REVERSED").Sum(m => m.Amount),
            TotalPostedCount = posted.Count,
            TotalPendingCount = movements.Count(m => m.Status == "PENDING_APPROVAL"),
            TotalRejectedCount = movements.Count(m => m.Status == "REJECTED"),
            TotalReversedCount = movements.Count(m => m.Status == "REVERSED"),
            ByCategory = posted
                .GroupBy(m => new { m.CategoryId, CategoryName = m.CategoryName ?? "Uncategorized", m.MovementType })
                .Select(g => new CashMovementCategoryBreakdownDto
                {
                    CategoryId = g.Key.CategoryId,
                    CategoryName = g.Key.CategoryName,
                    MovementType = g.Key.MovementType,
                    Count = g.Count(),
                    TotalAmount = g.Sum(x => x.Amount)
                })
                .OrderByDescending(x => x.TotalAmount)
                .ToList()
        };

        return Ok(report);
    }

    [HttpPost("close-shift")]
    public async Task<IActionResult> CloseShift([FromBody] CloseShiftDto request, CancellationToken cancellationToken = default)
    {
        if (request.ActualCountedBalance < 0)
            return BadRequest("Actual counted cash balance cannot be negative.");

        using var tx = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var shift = await _context.CashDrawerShifts
                .Include(s => s.Movements)
                .FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId, cancellationToken);

            if (shift == null || shift.Status != "OPEN")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Invalid or already closed shift.");
            }

            int pendingCount = shift.Movements.Count(m => m.Status == "PENDING_APPROVAL");
            if (pendingCount > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Cannot close shift while {pendingCount} cash movement(s) are still pending supervisor approval. Please approve or reject them first.");
            }

            RecalculateShiftExpectedBalance(shift);
            shift.ActualCountedBalance = Math.Round(request.ActualCountedBalance, 2);
            shift.Discrepancy = Math.Round(shift.ActualCountedBalance.Value - shift.ExpectedBalance, 2);
            shift.Status = "CLOSED";
            shift.ClosedAt = DateTime.UtcNow;
            shift.Notes = request.Notes;

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return Ok(new
            {
                Message = "Cash drawer shift closed successfully.",
                ShiftId = shift.ShiftId,
                OpeningCash = shift.OpeningFloat,
                CashSales = shift.TotalCashSales,
                CashPaidIn = shift.TotalCashIn,
                CashPaidOut = shift.TotalCashOut,
                CashDrops = shift.TotalCashDrops,
                CashRefunds = shift.TotalCashRefunds,
                ExpectedCash = shift.ExpectedBalance,
                ActualCountedCash = shift.ActualCountedBalance,
                Discrepancy = shift.Discrepancy
            });
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to close shift: {ex.Message}");
        }
    }

    public static CashDrawerShiftDto MapShiftToDto(CashDrawerShift shift)
    {
        var movements = (shift.Movements ?? Array.Empty<CashDrawerMovement>())
            .Where(m => m.MovementType != "CASH_SALE" && m.MovementType != "CASH_REFUND")
            .OrderByDescending(m => m.CreatedAt)
            .Select(MapMovementToDto)
            .ToList();

        decimal pettyCashOut = (shift.Movements ?? Array.Empty<CashDrawerMovement>())
            .Where(m => (m.Status ?? "POSTED") == "POSTED" && m.MovementType == "PETTY_CASH")
            .Sum(m => m.Amount);

        return new CashDrawerShiftDto
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            TerminalId = shift.TerminalId,
            CashierId = shift.CashierId,
            OpeningFloat = shift.OpeningFloat,
            TotalCashSales = shift.TotalCashSales,
            TotalCashRefunds = shift.TotalCashRefunds,
            TotalCashIn = shift.TotalCashIn,
            TotalCashOut = shift.TotalCashOut,
            TotalCashDrops = shift.TotalCashDrops,
            TotalPettyCashOut = pettyCashOut,
            ExpectedBalance = shift.ExpectedBalance,
            ActualCountedBalance = shift.ActualCountedBalance,
            Discrepancy = shift.Discrepancy,
            Status = shift.Status,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            Notes = shift.Notes,
            PendingApprovalsCount = movements.Count(m => m.Status == "PENDING_APPROVAL"),
            Movements = movements
        };
    }

    public static CashMovementResponseDto MapMovementToDto(CashDrawerMovement m) =>
        new()
        {
            MovementId = m.MovementId,
            ShiftId = m.ShiftId,
            BranchId = m.BranchId,
            TerminalId = m.TerminalId,
            MovementType = m.MovementType,
            CategoryId = m.CategoryId,
            CategoryName = m.CategoryName,
            Amount = m.Amount,
            Reason = m.Reason,
            ReferenceNumber = m.ReferenceNumber ?? $"CMV-{m.MovementId:D4}",
            ClientMovementId = m.ClientMovementId,
            Status = m.Status ?? "POSTED",
            CreatedBy = m.CreatedBy ?? m.AuthorizedBy,
            AuthorizedBy = m.AuthorizedBy,
            ApprovedBy = m.ApprovedBy,
            CreatedAt = m.CreatedAt,
            ApprovedAt = m.ApprovedAt,
            PostedAt = m.PostedAt ?? (m.Status == "POSTED" ? m.CreatedAt : null),
            RejectionReason = m.RejectionReason,
            ReversedBy = m.ReversedBy,
            ReversedAt = m.ReversedAt,
            ReversalReason = m.ReversalReason,
            OriginalMovementId = m.OriginalMovementId,
            SyncStatus = "SYNCED"
        };
}
