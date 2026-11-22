using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using POS.Api.Models;
using POS.Shared.Models;
using POS.Shared.Services;
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
[Authorize(Policy = POS.Shared.Constants.PosPermissions.ClientOperationsPolicy)]
public class CashDrawerController : ControllerBase
{
    private readonly PosDbContext _context;
    private static volatile bool _overnightRecoverySchemaEnsured;

    public CashDrawerController(PosDbContext context)
    {
        _context = context;
    }

    internal static async Task EnsureOvernightRecoverySchemaAsync(PosDbContext db, CancellationToken cancellationToken = default)
    {
        if (_overnightRecoverySchemaEnsured || !db.Database.IsNpgsql())
            return;
        try
        {
            var conn = db.Database.GetDbConnection();
            bool shouldClose = conn.State != ConnectionState.Open;
            if (shouldClose) await conn.OpenAsync(cancellationToken);
            try
            {
                if (conn is Npgsql.NpgsqlConnection npgConn)
                {
                    POS.Api.MigrationRunner.ApplyOvernightShiftRecoverySchema(npgConn);
                    _overnightRecoverySchemaEnsured = true;
                }
            }
            finally
            {
                if (shouldClose && conn.State == ConnectionState.Open)
                    await conn.CloseAsync();
            }
        }
        catch
        {
            // Non-fatal if DB permissions restrict DDL; Startup migration or DBA SQL handles it
        }
    }

    private string? GetAuthenticatedUsername() =>
        User.FindFirstValue(ClaimTypes.Name) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);

    private string GetAuthenticatedRole() =>
        User.FindFirstValue(ClaimTypes.Role) ?? string.Empty;

    public static bool IsSupervisorRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var clean = role.Trim();
        return clean.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase);
    }

    public static CashDrawerShiftDto? RedactForCashier(CashDrawerShiftDto? dto)
    {
        if (dto == null) return null;
        if (dto.ConfiguredNonCashMethods.Count == 0 && dto.PaymentReconciliations.Count > 0)
        {
            dto.ConfiguredNonCashMethods = dto.PaymentReconciliations
                .Where(r => !r.IsCash)
                .Select(r => new PaymentMethodDeclarationDto
                {
                    PaymentMethodCode = r.PaymentMethodCode,
                    PaymentMethodName = r.PaymentMethodName,
                    IsActive = r.IsActive,
                    DisplayOrder = r.DisplayOrder,
                    DeclaredAmount = r.DeclaredAmount
                })
                .ToList();
        }
        dto.IsBlindCloseRedacted = true;
        dto.ExpectedBalance = 0m;
        dto.TotalCashSales = 0m;
        dto.TotalCashRefunds = 0m;
        dto.Discrepancy = null;
        dto.TotalNonCashExpected = 0m;
        dto.TotalNetSales = 0m;
        dto.PaymentReconciliations = new();
        dto.OriginalDeclaration = null;
        dto.RecountHistory = new();
        dto.XReading = null;
        return dto;
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

    [AllowAnonymous]
    [HttpGet("terminal-lock/{terminalId:int}")]
    public async Task<ActionResult<TerminalClosingLockDto>> GetTerminalLockStatus(
        int terminalId,
        [FromQuery] string? cashierUsername = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureOvernightRecoverySchemaAsync(_context, cancellationToken);
        var status = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(
            _context, terminalId, cashierUsername, cancellationToken);
        return Ok(status);
    }

    [HttpGet("opening-fund-config/{terminalId:int}")]
    public async Task<ActionResult<TerminalCashFundConfigDto>> GetOpeningFundConfig(int terminalId, CancellationToken cancellationToken = default)
    {
        if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != terminalId.ToString())
            return Forbid();

        var terminal = await _context.Terminals
            .AsNoTracking()
            .Include(t => t.Outlet)
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.TerminalId == terminalId, cancellationToken);

        if (terminal == null)
            return NotFound("Terminal not found.");

        return Ok(new TerminalCashFundConfigDto
        {
            TerminalId = terminal.TerminalId,
            TerminalCode = terminal.TerminalCode,
            TerminalName = terminal.Name,
            OutletId = terminal.OutletId,
            OutletName = terminal.Outlet?.Name ?? string.Empty,
            BranchId = terminal.BranchId,
            BranchName = terminal.Branch?.Name ?? string.Empty,
            DefaultOpeningFund = terminal.Outlet?.DefaultOpeningFund,
            OpeningFundOverride = terminal.OpeningFundOverride,
            UpdatedBy = terminal.OpeningFundOverride.HasValue
                ? terminal.CashFundUpdatedBy
                : (terminal.Outlet?.DefaultOpeningFund.HasValue == true ? terminal.Outlet.CashFundUpdatedBy : terminal.CashFundUpdatedBy),
            UpdatedAt = terminal.OpeningFundOverride.HasValue
                ? terminal.CashFundUpdatedAt
                : (terminal.Outlet?.DefaultOpeningFund.HasValue == true ? terminal.Outlet.CashFundUpdatedAt : terminal.CashFundUpdatedAt)
        });
    }

    private static DateTime NormalizeUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    [HttpPost("open-shift")]
    public async Task<IActionResult> OpenShift([FromBody] OpenShiftDto request, CancellationToken cancellationToken = default)
    {
        var caller = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(caller)) return Unauthorized();
        bool isTerminalTokenForTerminal = string.Equals(GetAuthenticatedRole(), "Terminal", StringComparison.OrdinalIgnoreCase)
            && User.FindFirst("terminal_id")?.Value == request.TerminalId.ToString();
        if (!isTerminalTokenForTerminal && request.CashierId != null && request.CashierId != caller) return Forbid();
        var cashierId = isTerminalTokenForTerminal && !string.IsNullOrWhiteSpace(request.CashierId)
            ? request.CashierId.Trim()
            : caller;
        if (request.OpenedAt.HasValue)
            request.OpenedAt = NormalizeUtc(request.OpenedAt.Value);

        List<CashDenominationLineDto>? validatedOpeningDenoms = null;
        if (request.OpeningDenominations is { Count: > 0 })
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.OpeningDenominations, out var normalizedDenoms, out decimal countedOpeningCash, out string? denomErr))
                return BadRequest(denomErr ?? "Invalid opening cash denomination breakdown.");
            validatedOpeningDenoms = normalizedDenoms;
            request.OpeningFloat = countedOpeningCash;
        }

        if (!request.OpeningFloat.HasValue || request.OpeningFloat < 0)
            return BadRequest("Explicit opening float is required; zero is allowed, negative is not.");
        if (request.ExpectedOpeningFund.HasValue && request.ExpectedOpeningFund.Value < 0m)
            return BadRequest("Expected opening fund cannot be negative.");
        if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString()) return Forbid();
        var maxAllowedOpenedAtUtc = (BusinessDayClock.UtcNow() > DateTime.UtcNow ? BusinessDayClock.UtcNow() : DateTime.UtcNow).AddMinutes(5);
        if (request.ClientShiftId?.Length > 100 || request.OpenedAt > maxAllowedOpenedAtUtc)
            return BadRequest("Invalid opening identity or timestamp.");

        var licCheck = await new POS.Api.Services.LicensingService(_context).VerifyTerminalCanOperateAsync(request.TerminalId, null, cancellationToken);
        if (!licCheck.Allowed)
            return Conflict(licCheck.ErrorMessage ?? "Terminal license is not valid for opening a new shift.");

        using var tx = await _context.Database.BeginTransactionAsync(_context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
        // Serialize opening with payment, replay and closing on the terminal row.
        await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(_context, request.TerminalId, cancellationToken);
        if (!string.IsNullOrWhiteSpace(request.ClientShiftId))
        {
            var previous = await _context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ClientShiftId == request.ClientShiftId, cancellationToken);
            if (previous != null)
            {
                if (previous.TerminalId != request.TerminalId || (request.BranchId > 0 && previous.BranchId != request.BranchId)
                    || previous.CashierId != cashierId || previous.OpeningFloat != Math.Round(request.OpeningFloat.Value, 2)
                    || (request.OpenedAt.HasValue && NormalizeUtc(previous.OpenedAt).Ticks / 10 != request.OpenedAt.Value.Ticks / 10))
                    return Conflict("Opening retry does not match the original shift.");
                var prevSnap = await ReadSnapshotAsync(_context, previous.ShiftId, cancellationToken);
                return Ok(IsSupervisorRole(GetAuthenticatedRole()) ? prevSnap : RedactForCashier(prevSnap));
            }
        }

        if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString()) return Forbid();

        var lockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(
            _context, request.TerminalId, cashierId, cancellationToken);
        bool isHistoricalOfflineOpen = request.OpenedAt.HasValue &&
            lockState.LockedAtUtc.HasValue &&
            request.OpenedAt.Value < NormalizeUtc(lockState.LockedAtUtc.Value);
        if (!isHistoricalOfflineOpen)
        {
            if (lockState.IsCashierRestricted)
                return Conflict(lockState.CashierRestrictionMessage ?? lockState.LockMessage);
            if (lockState.IsTerminalLocked)
                return Conflict(lockState.LockMessage ?? "POS Terminal is locked for the remainder of the current business day.");
            if (lockState.HasUnresolvedPreviousDayShift)
                return Conflict(lockState.UnresolvedShiftMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
            if (lockState.IsShiftOwnedByAnotherCashier)
                return Conflict(lockState.ActiveShiftOwnershipMessage ?? TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);
        }

        var outlet = await _context.Terminals.AsNoTracking()
            .Include(o => o.Outlet)
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

        decimal? configuredOpeningFund = outlet?.OpeningFundOverride ?? outlet?.Outlet?.DefaultOpeningFund;
        decimal? resolvedExpectedOpeningFund = request.ExpectedOpeningFund.HasValue
            ? Math.Round(request.ExpectedOpeningFund.Value, 2, MidpointRounding.AwayFromZero)
            : configuredOpeningFund;

        if (request.RequireConfiguredFund && !resolvedExpectedOpeningFund.HasValue)
            return BadRequest("Initial Cash Fund is not configured for this Terminal or Outlet. Please contact an Administrator.");

        var existingOpen = await _context.CashDrawerShifts
            .FirstOrDefaultAsync(s => s.TerminalId == request.TerminalId && s.Status == "OPEN", cancellationToken);

        if (existingOpen != null)
        {
            return Conflict(existingOpen.OpeningConfirmed
                ? "This drawer already has an open shift."
                : "Reconcile and close the legacy shift in My Shift before confirming a new opening float. Historical opening cash cannot be changed.");
        }

        var openingTime = request.OpenedAt ?? BusinessDayClock.UtcNow();
        if (await _context.CashDrawerShifts.AnyAsync(s => s.TerminalId == request.TerminalId && (s.ClosedAt == null || s.ClosedAt >= openingTime), cancellationToken))
            return Conflict("Opening overlaps an existing drawer lifecycle.");
        decimal openingFloat = Math.Round(request.OpeningFloat.Value, 2, MidpointRounding.AwayFromZero);
        decimal finalExpectedOpening = resolvedExpectedOpeningFund ?? openingFloat;
        decimal openingDiscrepancy = Math.Round(openingFloat - finalExpectedOpening, 2, MidpointRounding.AwayFromZero);
        var shift = new CashDrawerShift
        {
            BranchId = request.BranchId > 0 ? request.BranchId : (outlet?.BranchId ?? 1),
            OutletId = outlet?.OutletId,
            TerminalId = request.TerminalId,
            CashierId = cashierId,
            BusinessDate = BusinessDayClock.GetBusinessDateString(openingTime),
            OpeningFloat = openingFloat,
            ExpectedOpeningFund = finalExpectedOpening,
            OpeningDiscrepancy = openingDiscrepancy,
            OpeningDenominationsJson = validatedOpeningDenoms != null ? JsonSerializer.Serialize(validatedOpeningDenoms) : null,
            OpeningConfirmed = true,
            ClientShiftId = request.ClientShiftId,
            ExpectedBalance = openingFloat,
            Status = "OPEN",
            ClosingType = TerminalClosingLockDto.ClosingTypeNormal,
            OpenedAt = openingTime,
            Notes = request.Notes
        };

        _context.CashDrawerShifts.Add(shift);
        await _context.SaveChangesAsync(cancellationToken);

        try
        {
            await POS.Api.Services.ZReadingService.EnsureTerminalBaselineProvisionedAsync(
                _context,
                request.TerminalId,
                $"AUTO_SHIFT_OPEN:{cashierId}",
                candidateTimestampUtc: openingTime,
                ct: cancellationToken);
        }
        catch
        {
            // Non-fatal if Z-Reading tables are absent in isolated unit tests
        }

        await tx.CommitAsync(cancellationToken);
        var openedSnap = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken);
        return Ok(IsSupervisorRole(GetAuthenticatedRole()) ? openedSnap : RedactForCashier(openedSnap));
    }

    [HttpGet("current")]
    public async Task<ActionResult<CashDrawerShiftDto>> GetCurrentShift([FromQuery] int terminalId, CancellationToken cancellationToken = default)
    {
        if (User.FindFirst("terminal_id")?.Value != terminalId.ToString() && !IsSupervisorRole(GetAuthenticatedRole())) return Forbid();
        var shift = await _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (shift == null) return NotFound("No active open shift found for this outlet.");

        var snapshot = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: true);
        if (snapshot == null) return NotFound("No active open shift found for this terminal.");
        return Ok(IsSupervisorRole(GetAuthenticatedRole()) ? snapshot : RedactForCashier(snapshot)!);
    }

    [HttpGet("shifts")]
    public async Task<ActionResult<List<CashDrawerShiftDto>>> GetShifts(
        [FromQuery] int? branchId = null,
        [FromQuery] int? terminalId = null,
        [FromQuery] string? cashierId = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? dateFrom = null,
        [FromQuery] DateTime? dateTo = null,
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!IsSupervisorRole(GetAuthenticatedRole())) return Forbid();

        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .AsQueryable();

        if (branchId.HasValue) query = query.Where(s => s.BranchId == branchId.Value);
        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(s => _context.Terminals.Any(t => t.TerminalId == s.TerminalId && t.OutletId == outId));
        }
        if (terminalId.HasValue) query = query.Where(s => s.TerminalId == terminalId.Value);
        if (!string.IsNullOrWhiteSpace(cashierId)) query = query.Where(s => s.CashierId == cashierId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(s => s.Status == status);
        if (dateFrom.HasValue) query = query.Where(s => s.OpenedAt >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(s => s.OpenedAt <= dateTo.Value);

        var shifts = await query
            .OrderByDescending(s => s.OpenedAt)
            .Take(200)
            .ToListAsync(cancellationToken);

        var results = new List<CashDrawerShiftDto>(shifts.Count);
        foreach (var s in shifts)
        {
            var dto = MapShiftToDto(s);
            await PopulateShiftReconciliationAndXReadingAsync(_context, s, dto, isReprint: false, cancellationToken);
            results.Add(dto);
        }

        return Ok(results);
    }

    [HttpGet("shifts/{shiftId:long}/totals")]
    public async Task<ActionResult<ShiftCashMovementSummaryDto>> GetShiftTotals(long shiftId, CancellationToken cancellationToken = default)
    {
        if (!IsSupervisorRole(GetAuthenticatedRole())) return Forbid();

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

            var movementLockState = await POS.Api.Services.TerminalClosingLockGuard.EvaluateTerminalLockAsync(
                _context, shift.TerminalId, cashierId, cancellationToken);
            if (movementLockState.IsCashierRestricted || movementLockState.IsTerminalLocked)
            {
                await tx.RollbackAsync(cancellationToken);
                return Conflict(movementLockState.CashierRestrictionMessage ?? movementLockState.LockMessage ?? "POS Terminal is locked for the remainder of the current business day.");
            }

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
                return BadRequest(IsSupervisorRole(callerRole)
                    ? $"Insufficient cash in drawer. Available expected cash is ₱{shift.ExpectedBalance:N2}, requested paid-out is ₱{amount:N2}."
                    : "Insufficient cash in drawer for the requested paid-out amount.");
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
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) && movement.BranchId != claimBranchId)
            return Forbid();
        var dto = MapMovementToDto(movement);
        await PopulateMovementHierarchyAsync(_context, [dto], cancellationToken);
        return Ok(dto);
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
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = _context.CashDrawerMovements
            .AsNoTracking()
            .Where(m => m.MovementType != "CASH_SALE" && m.MovementType != "CASH_REFUND")
            .AsQueryable();

        if (shiftId.HasValue) query = query.Where(m => m.ShiftId == shiftId.Value);
        if (branchId.HasValue) query = query.Where(m => m.BranchId == branchId.Value);
        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(m => _context.Terminals.Any(t => t.TerminalId == m.TerminalId && t.OutletId == outId));
        }
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

        var dtos = items.Select(MapMovementToDto).ToList();
        await PopulateMovementHierarchyAsync(_context, dtos, cancellationToken);
        return Ok(dtos);
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
        CancellationToken cancellationToken = default,
        [FromQuery] int? outletId = null)
    {
        if (!IsSupervisorRole(GetAuthenticatedRole())) return Forbid();

        if (!User.IsInRole("Admin") && int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId))
        {
            if (branchId.HasValue && branchId.Value != claimBranchId)
                return Forbid();
            branchId = claimBranchId;
        }

        var query = _context.CashDrawerMovements
            .AsNoTracking()
            .Where(m => m.MovementType != "CASH_SALE" && m.MovementType != "CASH_REFUND")
            .AsQueryable();

        if (branchId.HasValue) query = query.Where(m => m.BranchId == branchId.Value);
        if (outletId.HasValue)
        {
            int outId = outletId.Value;
            query = query.Where(m => _context.Terminals.Any(t => t.TerminalId == m.TerminalId && t.OutletId == outId));
        }
        if (terminalId.HasValue) query = query.Where(m => m.TerminalId == terminalId.Value);
        if (!string.IsNullOrWhiteSpace(cashierId))
            query = query.Where(m => m.CreatedBy == cashierId || m.AuthorizedBy == cashierId);
        if (dateFrom.HasValue) query = query.Where(m => m.CreatedAt >= dateFrom.Value);
        if (dateTo.HasValue) query = query.Where(m => m.CreatedAt <= dateTo.Value);

        var movements = await query.ToListAsync(cancellationToken);
        var posted = movements.Where(m => (m.Status ?? "POSTED") == "POSTED" && m.MovementType != "REVERSAL").ToList();

        var branchesById = await _context.Branches.AsNoTracking().ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var outletsById = await _context.Outlets.AsNoTracking().ToDictionaryAsync(o => o.OutletId, cancellationToken);
        var terminalsById = await _context.Terminals.AsNoTracking().ToDictionaryAsync(t => t.TerminalId, cancellationToken);

        var byTerminal = posted
            .GroupBy(m => (m.BranchId, m.TerminalId))
            .Select(g =>
            {
                terminalsById.TryGetValue(g.Key.TerminalId, out var term);
                int outId = term?.OutletId ?? 0;
                string outName = outletsById.TryGetValue(outId, out var outl)
                    ? outl.Name
                    : (outId > 0 ? $"Outlet #{outId}" : "Unknown / Legacy Outlet");
                int brId = g.Key.BranchId > 0 ? g.Key.BranchId : (term?.BranchId ?? 0);
                string brName = branchesById.TryGetValue(brId, out var br)
                    ? br.Name
                    : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
                string tCode = term?.TerminalCode ?? (g.Key.TerminalId > 0 ? $"LEGACY-{g.Key.TerminalId}" : "LEGACY");
                string tName = term?.Name ?? (g.Key.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{g.Key.TerminalId})" : "Unknown / Legacy Terminal");

                return new CashMovementTerminalBreakdownDto
                {
                    BranchId = brId,
                    BranchName = brName,
                    OutletId = outId,
                    OutletName = outName,
                    TerminalId = g.Key.TerminalId,
                    TerminalCode = tCode,
                    TerminalName = tName,
                    PostedCount = g.Count(),
                    TotalPaidIn = g.Where(m => m.MovementType == "CASH_IN").Sum(m => m.Amount),
                    TotalPaidOut = g.Where(m => m.MovementType is "CASH_OUT" or "PETTY_CASH").Sum(m => m.Amount),
                    TotalPettyCash = g.Where(m => m.MovementType == "PETTY_CASH").Sum(m => m.Amount),
                    TotalCashDrops = g.Where(m => m.MovementType == "CASH_DROP").Sum(m => m.Amount)
                };
            })
            .OrderBy(t => t.BranchName)
            .ThenBy(t => t.OutletName)
            .ThenBy(t => t.TerminalName)
            .ToList();

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
                .ToList(),
            ByTerminal = byTerminal
        };

        return Ok(report);
    }

    [HttpGet("shifts/{shiftId:long}/x-reading")]
    public async Task<ActionResult<XReadingReportDto>> GetShiftXReading(
        long shiftId,
        [FromQuery] bool reprint = false,
        [FromQuery] string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        bool authorizedSupervisor = IsSupervisorRole(GetAuthenticatedRole());
        if (!authorizedSupervisor && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifySupervisorPinAsync(supervisorPin, cancellationToken);
            authorizedSupervisor = sup != null;
        }
        if (!authorizedSupervisor)
            return Forbid();

        var shift = await _context.CashDrawerShifts.AsNoTracking().FirstOrDefaultAsync(s => s.ShiftId == shiftId, cancellationToken);
        if (shift == null) return NotFound("Shift not found.");

        var snapshot = await ReadSnapshotAsync(_context, shiftId, cancellationToken, requireOpen: false, isReprint: reprint);
        if (snapshot?.XReading == null) return NotFound("X-Reading report not available for this shift.");
        return Ok(snapshot.XReading);
    }

    [HttpGet("shifts/{shiftId:long}/declaration")]
    public async Task<ActionResult<CashierDeclarationReportDto>> GetShiftDeclaration(
        long shiftId,
        [FromQuery] bool reprint = false,
        CancellationToken cancellationToken = default)
    {
        var shift = await _context.CashDrawerShifts.AsNoTracking().FirstOrDefaultAsync(s => s.ShiftId == shiftId, cancellationToken);
        if (shift == null) return NotFound("Shift not found.");
        if (!IsSupervisorRole(GetAuthenticatedRole()) && User.FindFirst("terminal_id")?.Value != shift.TerminalId.ToString())
            return Forbid();
        if (!IsSupervisorRole(GetAuthenticatedRole()) && !string.Equals(shift.CashierId, GetAuthenticatedUsername(), StringComparison.OrdinalIgnoreCase))
            return Forbid();

        var snapshot = await ReadSnapshotAsync(_context, shiftId, cancellationToken, requireOpen: false, isReprint: reprint);
        if (snapshot?.CashierDeclaration == null) return NotFound("Cashier declaration acknowledgment not available for this shift.");
        return Ok(snapshot.CashierDeclaration);
    }

    [HttpPost("shifts/{shiftId:long}/recount")]
    public async Task<ActionResult<CashDrawerShiftDto>> RecountShift(
        long shiftId,
        [FromBody] ShiftRecountRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            return BadRequest("Recount request is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A reason is required for a shift recount or correction.");

        var caller = GetAuthenticatedUsername();
        if (string.IsNullOrEmpty(caller)) return Unauthorized();

        string? supervisorUsername = null;
        if (!string.IsNullOrWhiteSpace(request.SupervisorPin))
        {
            var sup = await VerifySupervisorPinAsync(request.SupervisorPin, cancellationToken);
            if (sup == null)
                return BadRequest("Invalid supervisor PIN or user does not have supervisor privileges.");
            supervisorUsername = sup.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            supervisorUsername = caller;
        }
        else
        {
            return Forbid();
        }

        bool hasDenominations = request.Denominations != null && request.Denominations.Count > 0;
        decimal countedCash;
        List<CashDenominationLineDto> validatedDenominations;
        if (hasDenominations)
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.Denominations, out var normDenoms, out countedCash, out var denomError))
                return BadRequest(denomError);
            validatedDenominations = normDenoms;
        }
        else
        {
            if (request.ActualCountedBalance < 0m)
                return BadRequest("Revised counted cash balance cannot be negative.");
            if (decimal.Round(request.ActualCountedBalance, 2) != request.ActualCountedBalance)
                return BadRequest("Revised counted cash balance supports at most two decimal places.");
            countedCash = Math.Round(request.ActualCountedBalance, 2, MidpointRounding.AwayFromZero);
            validatedDenominations = CashDenominationCatalog.CreateDefaultLines();
        }

        if (request.PaymentDeclarations != null)
        {
            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decl in request.PaymentDeclarations)
            {
                if (decl == null) continue;
                string code = (decl.PaymentMethodCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code))
                    return BadRequest("Payment method code is required for all payment declarations.");
                if (code == "CASH") continue;
                if (!seenMethods.Add(code))
                    return BadRequest($"Duplicate payment declaration for {code}.");
                if (decl.DeclaredAmount < 0m)
                    return BadRequest($"Declared total for {code} cannot be negative.");
                if (decimal.Round(decl.DeclaredAmount, 2) != decl.DeclaredAmount)
                    return BadRequest($"Declared total for {code} supports at most two decimal places.");
            }
        }

        using var tx = await _context.Database.BeginTransactionAsync(_context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
        try
        {
            var shift = await _context.CashDrawerShifts
                .Include(s => s.Movements)
                .FirstOrDefaultAsync(s => s.ShiftId == shiftId, cancellationToken);

            if (shift == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return NotFound("Shift not found.");
            }
            if (shift.Status != "CLOSED")
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Only a closed shift can undergo a supervisor recount or correction.");
            }

            RecalculateShiftExpectedBalance(shift);

            // Load pre-recount values
            var prevDenoms = new List<CashDenominationLineDto>();
            if (!string.IsNullOrWhiteSpace(shift.ClosingDenominationsJson))
            {
                try { prevDenoms = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(shift.ClosingDenominationsJson) ?? new(); }
                catch { prevDenoms = new(); }
            }
            var prevRecons = new List<PaymentMethodReconciliationDto>();
            if (!string.IsNullOrWhiteSpace(shift.ClosingPaymentReconciliationsJson))
            {
                try { prevRecons = JsonSerializer.Deserialize<List<PaymentMethodReconciliationDto>>(shift.ClosingPaymentReconciliationsJson) ?? new(); }
                catch { prevRecons = new(); }
            }
            var prevDeclarations = prevRecons
                .Where(r => !r.IsCash)
                .Select(r => new PaymentMethodDeclarationDto
                {
                    PaymentMethodCode = r.PaymentMethodCode,
                    PaymentMethodName = r.PaymentMethodName,
                    IsActive = r.IsActive,
                    DisplayOrder = r.DisplayOrder,
                    DeclaredAmount = r.DeclaredAmount
                })
                .ToList();

            decimal prevCountedCash = shift.ActualCountedBalance ?? 0m;
            decimal prevVariance = shift.Discrepancy ?? Math.Round(prevCountedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

            // Ensure original declaration is immutably preserved
            if (string.IsNullOrWhiteSpace(shift.ClosingOriginalDeclarationJson))
            {
                var origDecl = new CashierDeclarationReportDto
                {
                    ShiftId = shift.ShiftId,
                    ClientShiftId = shift.ClientShiftId,
                    BranchId = shift.BranchId,
                    TerminalId = shift.TerminalId,
                    CashierId = shift.CashierId,
                    OpenedAt = shift.OpenedAt,
                    ClosedAt = shift.ClosedAt,
                    SubmittedAt = shift.ClosedAt ?? DateTime.UtcNow,
                    TotalCountedCash = prevCountedCash,
                    TotalNonCashDeclared = prevDeclarations.Sum(d => d.DeclaredAmount),
                    Denominations = prevDenoms,
                    PaymentDeclarations = prevDeclarations,
                    Notes = shift.Notes
                };
                shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(origDecl);
            }

            // Apply revised values
            shift.ActualCountedBalance = countedCash;
            shift.Discrepancy = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
            var revisedRecons = await BuildPaymentReconciliationsAsync(_context, shift, request.PaymentDeclarations, cancellationToken);
            var revisedDeclarations = revisedRecons
                .Where(r => !r.IsCash)
                .Select(r => new PaymentMethodDeclarationDto
                {
                    PaymentMethodCode = r.PaymentMethodCode,
                    PaymentMethodName = r.PaymentMethodName,
                    IsActive = r.IsActive,
                    DisplayOrder = r.DisplayOrder,
                    DeclaredAmount = r.DeclaredAmount
                })
                .ToList();

            var history = new List<ShiftRecountAuditDto>();
            if (!string.IsNullOrWhiteSpace(shift.ClosingRecountHistoryJson))
            {
                try { history = JsonSerializer.Deserialize<List<ShiftRecountAuditDto>>(shift.ClosingRecountHistoryJson) ?? new(); }
                catch { history = new(); }
            }

            history.Add(new ShiftRecountAuditDto
            {
                RecountNumber = history.Count + 1,
                ShiftId = shift.ShiftId,
                CorrectedBy = supervisorUsername!,
                CorrectedAt = DateTime.UtcNow,
                Reason = request.Reason.Trim(),
                OriginalCountedCash = prevCountedCash,
                RevisedCountedCash = countedCash,
                OriginalCashVariance = prevVariance,
                RevisedCashVariance = shift.Discrepancy.Value,
                OriginalDenominations = prevDenoms,
                RevisedDenominations = validatedDenominations,
                OriginalPaymentDeclarations = prevDeclarations,
                RevisedPaymentDeclarations = revisedDeclarations
            });

            shift.ClosingDenominationsJson = JsonSerializer.Serialize(validatedDenominations);
            shift.ClosingPaymentReconciliationsJson = JsonSerializer.Serialize(revisedRecons);
            shift.ClosingRecountHistoryJson = JsonSerializer.Serialize(history);
            if (!string.IsNullOrWhiteSpace(request.Notes))
            {
                var trimmed = request.Notes.Trim();
                shift.Notes = trimmed.Length > 255 ? trimmed[..255] : trimmed;
            }

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var updatedDto = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
            return Ok(updatedDto);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to record shift recount: {ex.Message}");
        }
    }

    private async Task<User?> VerifySupervisorPinOrHashAsync(
        string? rawPin,
        string? pinHash,
        string? expectedUsername = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(rawPin))
            return await VerifySupervisorPinAsync(rawPin, cancellationToken);

        if (string.IsNullOrWhiteSpace(pinHash))
            return null;

        var usersWithPins = await _context.Users
            .AsNoTracking()
            .Where(u => u.PinCode != null && u.PinCode != "")
            .ToListAsync(cancellationToken);

        return usersWithPins.FirstOrDefault(u =>
            IsSupervisorRole(u.Role) &&
            (string.IsNullOrWhiteSpace(expectedUsername) || string.Equals(u.Username, expectedUsername.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            POS.Shared.Helpers.VoidAuthorizationHelper.FixedTimeEqualsHex(
                POS.Shared.Helpers.VoidAuthorizationHelper.ComputePinHash(u.PinCode!),
                pinHash.Trim()));
    }

    /// <summary>
    /// Anytime strictly read-only terminal X-Reading audit snapshot for the current (or specified) Business Date.
    /// Can be generated repeatedly even while cashier shifts are actively OPEN, spanning multiple shifts/cashiers.
    /// NEVER closes shifts, locks cashiers/terminals, resets counters, or mutates any database state.
    /// </summary>
    [HttpGet("x-reading")]
    public async Task<ActionResult<XReadingReportDto>> GetAnytimeXReading(
        [FromQuery] int terminalId,
        [FromQuery] string? businessDate = null,
        [FromQuery] string? supervisorPin = null,
        [FromQuery] bool reprint = false,
        CancellationToken cancellationToken = default)
    {
        if (terminalId <= 0)
            return BadRequest("Valid terminalId is required.");

        bool authorized = IsSupervisorRole(GetAuthenticatedRole());
        if (!authorized && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifySupervisorPinAsync(supervisorPin, cancellationToken);
            authorized = sup != null;
        }
        if (!authorized)
            return Forbid();

        var terminal = await _context.Terminals
            .AsNoTracking()
            .Include(t => t.Outlet)
            .Include(t => t.Branch)
            .FirstOrDefaultAsync(t => t.TerminalId == terminalId, cancellationToken);
        if (terminal == null)
            return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
        {
            return Forbid();
        }

        var nowUtc = BusinessDayClock.UtcNow();
        DateOnly targetDate;
        if (!string.IsNullOrWhiteSpace(businessDate))
        {
            if (!DateOnly.TryParseExact(businessDate.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out targetDate))
                return BadRequest("Invalid businessDate format. Expected yyyy-MM-dd.");
        }
        else
        {
            targetDate = BusinessDayClock.GetBusinessDate(nowUtc);
        }

        var (periodStartUtc, periodEndUtc) = BusinessDayClock.GetPeriodUtc(targetDate);
        var effectiveEndUtc = nowUtc < periodEndUtc ? nowUtc : periodEndUtc;
        var periodStart = DateTime.SpecifyKind(periodStartUtc, DateTimeKind.Unspecified);
        var periodEnd = DateTime.SpecifyKind(effectiveEndUtc, DateTimeKind.Unspecified);
        var fullPeriodEnd = DateTime.SpecifyKind(periodEndUtc, DateTimeKind.Unspecified);

        var dayShifts = await _context.CashDrawerShifts
            .AsNoTracking()
            .Include(s => s.Movements)
            .Where(s => s.TerminalId == terminalId &&
                ((s.OpenedAt >= periodStart && s.OpenedAt < fullPeriodEnd) ||
                 (s.ClosedAt.HasValue && s.ClosedAt.Value >= periodStart && s.ClosedAt.Value < fullPeriodEnd &&
                  s.ClosingType != TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery) ||
                 (s.Status == "OPEN" && s.OpenedAt < fullPeriodEnd)))
            .OrderBy(s => s.OpenedAt)
            .ToListAsync(cancellationToken);

        var dayShiftIds = dayShifts.Select(s => s.ShiftId).ToHashSet();

        var periodOrders = await _context.Orders
            .AsNoTracking()
            .Include(o => o.Payments)
            .Where(o => o.TerminalId == terminalId && o.CreatedAt >= periodStart && o.CreatedAt <= periodEnd)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        if (dayShiftIds.Count > 0)
        {
            var shiftOrdersExtra = await _context.Orders
                .AsNoTracking()
                .Include(o => o.Payments)
                .Where(o => o.ShiftId.HasValue && dayShiftIds.Contains(o.ShiftId.Value))
                .OrderBy(o => o.CreatedAt)
                .ToListAsync(cancellationToken);
            var existingOrderIds = periodOrders.Select(o => o.OrderId).ToHashSet();
            foreach (var so in shiftOrdersExtra)
            {
                if (existingOrderIds.Add(so.OrderId))
                    periodOrders.Add(so);
            }
        }

        var periodOrderIds = periodOrders.Select(o => o.OrderId).ToList();
        var returnRefunds = periodOrderIds.Count > 0
            ? await _context.Refunds
                .AsNoTracking()
                .Include(r => r.OriginalPayment)
                .Where(r => r.VoidId == null && r.Status == "COMPLETED" && r.OriginalPayment != null && periodOrderIds.Contains(r.OriginalPayment.OrderId))
                .ToListAsync(cancellationToken)
            : new List<Refund>();

        int returnCount = periodOrderIds.Count > 0
            ? await _context.SalesReturns
                .AsNoTracking()
                .CountAsync(sr => periodOrderIds.Contains(sr.OriginalOrderId) && sr.Status == "COMPLETED", cancellationToken)
            : 0;

        var paidOrders = periodOrders.Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "VOIDED").ToList();
        var voidOrders = periodOrders.Where(o => o.PaymentStatus == "VOIDED" || o.OrderStatus == "VOIDED").ToList();

        decimal grossSales = Math.Round(paidOrders.Sum(o => o.Subtotal) + voidOrders.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero);
        decimal totalDiscounts = Math.Round(paidOrders.Sum(o => o.DiscountAmount), 2, MidpointRounding.AwayFromZero);
        decimal voidTotal = Math.Round(voidOrders.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero);
        decimal totalReturnRefunds = Math.Round(returnRefunds.Sum(r => r.RefundAmount), 2, MidpointRounding.AwayFromZero);
        decimal totalRefundsAndVoids = Math.Round(voidTotal + totalReturnRefunds, 2, MidpointRounding.AwayFromZero);
        decimal netSales = Math.Round(paidOrders.Sum(o => o.TotalAmount) - totalReturnRefunds, 2, MidpointRounding.AwayFromZero);

        var invoices = periodOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.InvoiceNumber))
            .OrderBy(o => o.InvoiceIssuedAt ?? o.CreatedAt)
            .ThenBy(o => o.OrderId)
            .Select(o => o.InvoiceNumber!)
            .ToList();

        decimal totalOpeningFloat = Math.Round(dayShifts.Sum(s => s.OpeningFloat), 2, MidpointRounding.AwayFromZero);
        decimal totalCashSales = Math.Round(dayShifts.Sum(s => s.TotalCashSales), 2, MidpointRounding.AwayFromZero);
        decimal totalCashRefunds = Math.Round(dayShifts.Sum(s => s.TotalCashRefunds), 2, MidpointRounding.AwayFromZero);
        decimal totalCashIn = Math.Round(dayShifts.Sum(s => s.TotalCashIn), 2, MidpointRounding.AwayFromZero);
        decimal totalCashOut = Math.Round(dayShifts.Sum(s => s.TotalCashOut), 2, MidpointRounding.AwayFromZero);
        decimal totalCashDrops = Math.Round(dayShifts.Sum(s => s.TotalCashDrops), 2, MidpointRounding.AwayFromZero);
        decimal expectedCashInDrawer = Math.Round(dayShifts.Sum(s => s.ExpectedBalance), 2, MidpointRounding.AwayFromZero);
        decimal countedCashInDrawer = Math.Round(dayShifts.Where(s => s.ActualCountedBalance.HasValue).Sum(s => s.ActualCountedBalance!.Value), 2, MidpointRounding.AwayFromZero);

        // If orders exist without shifts or shifts haven't tallied yet, compute cash sales/refunds from payments
        if (dayShifts.Count == 0 && periodOrders.Count > 0)
        {
            totalCashSales = Math.Round(periodOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase)
                    && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded))
                .Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            decimal cashVoidRef = periodOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, "CASH", StringComparison.OrdinalIgnoreCase)
                    && p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded)
                .Sum(p => p.Amount);
            decimal cashRetRef = returnRefunds
                .Where(r => string.Equals(r.RefundMethod, "CASH", StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.RefundAmount);
            totalCashRefunds = Math.Round(cashVoidRef + cashRetRef, 2, MidpointRounding.AwayFromZero);
            expectedCashInDrawer = Math.Round(totalCashSales - totalCashRefunds, 2, MidpointRounding.AwayFromZero);
        }

        // Build tender breakdown across the entire Business Date
        var configuredMethods = await _context.PaymentMethods
            .AsNoTracking()
            .OrderBy(pm => pm.DisplayOrder)
            .ThenBy(pm => pm.Name)
            .ToListAsync(cancellationToken);

        var reconciliations = new List<PaymentMethodReconciliationDto>();
        decimal cashNet = Math.Round(totalCashSales - totalCashRefunds, 2, MidpointRounding.AwayFromZero);
        decimal closedExpectedCash = Math.Round(dayShifts.Where(s => s.Status == "CLOSED").Sum(s => s.ExpectedBalance), 2, MidpointRounding.AwayFromZero);
        int activeShiftCount = dayShifts.Count(s => s.Status == "OPEN");
        int closedShiftCount = dayShifts.Count(s => s.Status == "CLOSED");
        decimal cashVariance = activeShiftCount == 0 && closedShiftCount > 0
            ? Math.Round(countedCashInDrawer - closedExpectedCash, 2, MidpointRounding.AwayFromZero)
            : 0m;

        reconciliations.Add(new PaymentMethodReconciliationDto
        {
            PaymentMethodCode = "CASH",
            PaymentMethodName = "Cash (Drawer)",
            IsCash = true,
            IsActive = true,
            DisplayOrder = 0,
            GrossSalesAmount = totalCashSales,
            RefundAmount = totalCashRefunds,
            NetSalesAmount = cashNet,
            ExpectedAmount = expectedCashInDrawer,
            DeclaredAmount = countedCashInDrawer,
            Variance = cashVariance,
            Status = activeShiftCount > 0 ? "IN_PROGRESS" : PaymentMethodReconciliationDto.ComputeStatus(cashVariance)
        });

        var nonCashCodes = new List<string>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CASH" };
        foreach (var pm in configuredMethods.Where(m => m.IsActive))
            if (seenCodes.Add(pm.Code)) nonCashCodes.Add(pm.Code);
        foreach (var p in periodOrders.SelectMany(o => o.Payments))
        {
            if (!string.IsNullOrWhiteSpace(p.PaymentMethod)
                && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded)
                && seenCodes.Add(p.PaymentMethod.Trim()))
            {
                nonCashCodes.Add(p.PaymentMethod.Trim().ToUpperInvariant());
            }
        }
        foreach (var r in returnRefunds)
        {
            if (!string.IsNullOrWhiteSpace(r.RefundMethod) && seenCodes.Add(r.RefundMethod.Trim()))
                nonCashCodes.Add(r.RefundMethod.Trim().ToUpperInvariant());
        }

        foreach (var code in nonCashCodes)
        {
            var cfg = configuredMethods.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
            decimal mGross = Math.Round(periodOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                    && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded))
                .Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            decimal mVoidRef = periodOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                    && p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded)
                .Sum(p => p.Amount);
            decimal mRetRef = returnRefunds
                .Where(r => string.Equals(r.RefundMethod, code, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.RefundAmount);
            decimal mRefund = Math.Round(mVoidRef + mRetRef, 2, MidpointRounding.AwayFromZero);
            decimal mNet = Math.Round(mGross - mRefund, 2, MidpointRounding.AwayFromZero);

            reconciliations.Add(new PaymentMethodReconciliationDto
            {
                PaymentMethodCode = cfg?.Code ?? code.ToUpperInvariant(),
                PaymentMethodName = cfg?.Name ?? code.ToUpperInvariant(),
                IsCash = false,
                IsActive = cfg?.IsActive ?? false,
                DisplayOrder = cfg?.DisplayOrder ?? 999,
                GrossSalesAmount = mGross,
                RefundAmount = mRefund,
                NetSalesAmount = mNet,
                ExpectedAmount = mNet,
                DeclaredAmount = 0m,
                Variance = 0m,
                Status = activeShiftCount > 0 ? "IN_PROGRESS" : "READ_ONLY"
            });
        }

        // Per-cashier breakdown across shifts and orders in this Business Date
        var refundsByOrderId = returnRefunds
            .Where(r => r.OriginalPayment != null)
            .GroupBy(r => r.OriginalPayment!.OrderId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.RefundAmount));
        var shiftCashierLookup = dayShifts.ToDictionary(s => s.ShiftId, s => s.CashierId);

        var cashierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in dayShifts) if (!string.IsNullOrWhiteSpace(s.CashierId)) cashierNames.Add(s.CashierId.Trim());
        foreach (var o in periodOrders)
        {
            string? c = !string.IsNullOrWhiteSpace(o.CashierId) ? o.CashierId
                : (o.ShiftId.HasValue && shiftCashierLookup.TryGetValue(o.ShiftId.Value, out var sc) ? sc : null);
            if (!string.IsNullOrWhiteSpace(c)) cashierNames.Add(c.Trim());
        }

        var cashierSummaries = new List<XReadingCashierSummaryDto>();
        foreach (var cashier in cashierNames.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var cShifts = dayShifts.Where(s => string.Equals(s.CashierId, cashier, StringComparison.OrdinalIgnoreCase)).ToList();
            var cShiftIds = cShifts.Select(s => s.ShiftId).ToHashSet();
            var cOrders = periodOrders.Where(o =>
                string.Equals(o.CashierId, cashier, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(o.CashierId) && o.ShiftId.HasValue && cShiftIds.Contains(o.ShiftId.Value))).ToList();
            var cPaid = cOrders.Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "VOIDED").ToList();
            var cVoid = cOrders.Where(o => o.PaymentStatus == "VOIDED" || o.OrderStatus == "VOIDED").ToList();
            decimal cReturns = cOrders.Sum(o => refundsByOrderId.TryGetValue(o.OrderId, out var rAmt) ? rAmt : 0m);

            cashierSummaries.Add(new XReadingCashierSummaryDto
            {
                CashierId = cashier,
                ShiftCount = cShifts.Count,
                OpenShifts = cShifts.Count(s => s.Status == "OPEN"),
                ClosedShifts = cShifts.Count(s => s.Status == "CLOSED"),
                TransactionCount = cPaid.Count,
                GrossSales = Math.Round(cPaid.Sum(o => o.Subtotal) + cVoid.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero),
                NetSales = Math.Round(cPaid.Sum(o => o.TotalAmount) - cReturns, 2, MidpointRounding.AwayFromZero)
            });
        }

        var openShift = dayShifts.LastOrDefault(s => s.Status == "OPEN");
        var report = new XReadingReportDto
        {
            ShiftId = openShift?.ShiftId ?? dayShifts.LastOrDefault()?.ShiftId ?? 0,
            ClientShiftId = openShift?.ClientShiftId ?? dayShifts.LastOrDefault()?.ClientShiftId,
            BranchId = terminal.BranchId,
            BranchName = terminal.Branch?.Name ?? $"Branch #{terminal.BranchId}",
            BranchCode = terminal.Branch?.Code,
            BranchAddress = terminal.Branch?.Address,
            BranchPhone = terminal.Branch?.Phone,
            OutletId = terminal.OutletId,
            OutletName = terminal.Outlet?.Name ?? $"Outlet #{terminal.OutletId}",
            TerminalId = terminal.TerminalId,
            TerminalCode = terminal.TerminalCode,
            TerminalName = terminal.Name,
            PTUNumber = terminal.Ptunumber,
            MINNumber = terminal.Minnumber,
            SerialNumber = terminal.SerialNumber,
            CashierId = activeShiftCount > 0
                ? (openShift?.CashierId ?? "ACTIVE SHIFT")
                : (cashierSummaries.Count == 1 ? cashierSummaries[0].CashierId : "ALL CASHIERS"),
            OpenedAt = dayShifts.FirstOrDefault()?.OpenedAt ?? periodStart,
            ClosedAt = activeShiftCount == 0 ? dayShifts.LastOrDefault()?.ClosedAt : null,
            GeneratedAt = nowUtc,
            BusinessDate = targetDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            IsAnytimeAudit = true,
            ActiveShiftCount = activeShiftCount,
            ClosedShiftCount = closedShiftCount,
            CashierSummaries = cashierSummaries,
            IsReprint = reprint,
            ReportingPeriodStart = periodStart,
            ReportingPeriodEnd = periodEnd,
            ReportingPeriodMatchesShift = dayShifts.Count == 1,
            BeginningInvoiceNumber = invoices.FirstOrDefault(),
            EndingInvoiceNumber = invoices.LastOrDefault(),
            CompletedTransactionsCount = paidOrders.Count,
            VoidedTransactionsCount = voidOrders.Count,
            ReturnTransactionsCount = returnCount,
            GrossSales = grossSales,
            TotalDiscounts = totalDiscounts,
            TotalRefundsAndVoids = totalRefundsAndVoids,
            NetSales = netSales,
            VatableSales = Math.Round(paidOrders.Sum(o => o.VatableSales), 2, MidpointRounding.AwayFromZero),
            VatExemptSales = Math.Round(paidOrders.Sum(o => o.VatExemptSales), 2, MidpointRounding.AwayFromZero),
            ZeroRatedSales = Math.Round(paidOrders.Sum(o => o.ZeroRatedSales), 2, MidpointRounding.AwayFromZero),
            VatAmount = Math.Round(paidOrders.Sum(o => o.VatAmount), 2, MidpointRounding.AwayFromZero),
            ShiftTransactionsCount = paidOrders.Count,
            ShiftGrossSales = grossSales,
            ShiftDiscounts = totalDiscounts,
            ShiftRefundsAndVoids = totalRefundsAndVoids,
            ShiftNetSales = netSales,
            ShiftCashNetSales = cashNet,
            ShiftNonCashNetSales = Math.Round(reconciliations.Where(r => !r.IsCash).Sum(r => r.NetSalesAmount), 2, MidpointRounding.AwayFromZero),
            OpeningFloat = totalOpeningFloat,
            CashSalesCollected = totalCashSales,
            CashRefunds = totalCashRefunds,
            NetCashSales = cashNet,
            TotalCashIn = totalCashIn,
            TotalCashOut = totalCashOut,
            TotalCashDrops = totalCashDrops,
            ExpectedCashInDrawer = expectedCashInDrawer,
            CountedCashInDrawer = countedCashInDrawer,
            CashVariance = cashVariance,
            CashReconciliationStatus = activeShiftCount > 0 ? "IN_PROGRESS" : PaymentMethodReconciliationDto.ComputeStatus(cashVariance),
            HasRecordedDenominations = false,
            PaymentReconciliations = reconciliations,
            IsOverallBalanced = activeShiftCount == 0 && cashVariance == 0m,
            OverallStatus = activeShiftCount > 0
                ? $"IN PROGRESS ({activeShiftCount} OPEN SHIFT)"
                : (closedShiftCount == 0 ? "NO SHIFTS" : PaymentMethodReconciliationDto.ComputeStatus(cashVariance))
        };

        return Ok(report);
    }

    [HttpGet("cashier-restrictions")]
    public async Task<ActionResult<List<CashierClosingRestrictionDto>>> GetCashierRestrictions(
        [FromQuery] int terminalId,
        [FromQuery] string? businessDate = null,
        CancellationToken cancellationToken = default)
    {
        if (terminalId <= 0)
            return BadRequest("Valid terminalId is required.");

        if (!IsSupervisorRole(GetAuthenticatedRole()) &&
            User.FindFirst("terminal_id")?.Value != terminalId.ToString())
            return Forbid();

        var terminal = await _context.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId, cancellationToken);
        if (terminal == null) return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
            return Forbid();

        string targetDate = !string.IsNullOrWhiteSpace(businessDate)
            ? businessDate.Trim()
            : BusinessDayClock.GetBusinessDateString(BusinessDayClock.UtcNow());

        var rows = await _context.CashierClosingRestrictions
            .AsNoTracking()
            .Where(r => r.TerminalId == terminalId && r.BusinessDate == targetDate)
            .OrderByDescending(r => r.ClosedAtUtc)
            .ToListAsync(cancellationToken);

        return Ok(rows.Select(POS.Api.Services.TerminalClosingLockGuard.MapRestrictionToDto).ToList());
    }

    /// <summary>
    /// Authorized supervisor correction/reopen workflow allowing a closed cashier to log back into the same terminal
    /// for the same Business Date. Preserves all closed CashDrawerShift accountability records immutably.
    /// </summary>
    [HttpPost("cashier-restrictions/reopen")]
    public async Task<ActionResult<CashierClosingRestrictionDto>> ReopenCashierRestriction(
        [FromBody] ReopenCashierRestrictionRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || request.TerminalId <= 0)
            return BadRequest("Valid terminalId is required.");
        if (string.IsNullOrWhiteSpace(request.CashierId))
            return BadRequest("CashierId is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A reason is required to reopen cashier access.");

        string? approverUsername = null;
        if (!string.IsNullOrWhiteSpace(request.SupervisorPin) || !string.IsNullOrWhiteSpace(request.SupervisorPinHash))
        {
            var sup = await VerifySupervisorPinOrHashAsync(request.SupervisorPin, request.SupervisorPinHash, request.ApprovedBy, cancellationToken);
            if (sup == null)
                return Forbid();
            approverUsername = sup.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            approverUsername = GetAuthenticatedUsername();
        }
        else
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(approverUsername))
            return Unauthorized();

        var terminal = await _context.Terminals.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, cancellationToken);
        if (terminal == null)
            return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
            return Forbid();

        string targetDate = !string.IsNullOrWhiteSpace(request.BusinessDate)
            ? request.BusinessDate.Trim()
            : BusinessDayClock.GetBusinessDateString(BusinessDayClock.UtcNow());

        var reopened = await POS.Api.Services.TerminalClosingLockGuard.ReopenCashierRestrictionAsync(
            _context,
            request.TerminalId,
            request.CashierId,
            targetDate,
            approverUsername,
            request.Reason,
            request.ClientOperationId,
            cancellationToken);

        if (reopened == null)
            return NotFound("No active cashier closing restriction found for this cashier, terminal, and business date.");

        return Ok(reopened);
    }

    /// <summary>
    /// Inspect an unresolved open cashier shift from a previous Business Date on the specified terminal.
    /// Supervisor-only (role or valid supervisor PIN) and branch-scoped. Never mutates state.
    /// </summary>
    [HttpGet("unresolved-shift/{terminalId:int}")]
    public async Task<ActionResult<CashDrawerShiftDto>> GetUnresolvedPreviousDayShift(
        int terminalId,
        [FromQuery] string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        if (terminalId <= 0)
            return BadRequest("Valid terminalId is required.");

        await EnsureOvernightRecoverySchemaAsync(_context, cancellationToken);

        bool authorized = IsSupervisorRole(GetAuthenticatedRole());
        if (!authorized && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifySupervisorPinAsync(supervisorPin, cancellationToken);
            authorized = sup != null;
        }
        if (!authorized)
            return Forbid();

        var terminal = await _context.Terminals.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TerminalId == terminalId, cancellationToken);
        if (terminal == null)
            return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
            return Forbid();

        var nowUtc = BusinessDayClock.UtcNow();
        var openShift = await _context.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .OrderByDescending(s => s.OpenedAt)
            .ThenByDescending(s => s.ShiftId)
            .FirstOrDefaultAsync(cancellationToken);

        if (openShift == null || !BusinessDayClock.IsPreviousBusinessDateShift(openShift.OpenedAt, nowUtc, openShift.BusinessDate))
            return NotFound("No unresolved previous-Business-Date cashier shift found for this terminal.");

        var snapshot = await ReadSnapshotAsync(_context, openShift.ShiftId, cancellationToken, requireOpen: true, isReprint: false);
        if (snapshot == null)
            return NotFound("Unresolved shift snapshot could not be loaded.");

        return Ok(snapshot);
    }

    /// <summary>
    /// Supervisor-assisted overnight closing for an unclosed cashier shift from a previous Business Date.
    /// Preserves the original cashier's transactions, opening fund, and Business Date, records supervisor recovery audit,
    /// applies the original cashier's Close Cashier restriction for the ORIGINAL Business Date only,
    /// and releases terminal ownership for the current Business Date without touching Z-Reading locks.
    /// </summary>
    [HttpPost("overnight-recovery-close")]
    public async Task<ActionResult<CashDrawerShiftDto>> SupervisorOvernightClose(
        [FromBody] SupervisorOvernightCloseRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || request.TerminalId <= 0)
            return BadRequest("Valid terminalId and recovery payload are required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A reason is required for supervisor-assisted overnight closing.");
        if (!request.IsDeclarationConfirmed)
            return BadRequest("Supervisor must explicitly confirm the physical cash count before closing.");
        if (request.Denominations == null || request.Denominations.Count == 0)
            return BadRequest("Actual physical cash denomination breakdown is required for supervisor-assisted overnight closing.");

        if (!CashDenominationCatalog.TryValidateAndCalculate(request.Denominations, out var validatedDenominations, out decimal countedCash, out var denomError))
            return BadRequest(denomError ?? "Invalid cash denomination breakdown.");

        if (request.PaymentDeclarations != null)
        {
            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decl in request.PaymentDeclarations)
            {
                if (decl == null) continue;
                string code = (decl.PaymentMethodCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code))
                    return BadRequest("Payment method code is required for all payment declarations.");
                if (code == "CASH") continue;
                if (!seenMethods.Add(code))
                    return BadRequest($"Duplicate payment declaration for {code}.");
                if (decl.DeclaredAmount < 0m)
                    return BadRequest($"Declared total for {code} cannot be negative.");
                if (decimal.Round(decl.DeclaredAmount, 2) != decl.DeclaredAmount)
                    return BadRequest($"Declared total for {code} supports at most two decimal places.");
            }
        }

        await EnsureOvernightRecoverySchemaAsync(_context, cancellationToken);

        string? supervisorUsername = null;
        if (!string.IsNullOrWhiteSpace(request.SupervisorPin) || !string.IsNullOrWhiteSpace(request.SupervisorPinHash))
        {
            var sup = await VerifySupervisorPinOrHashAsync(request.SupervisorPin, request.SupervisorPinHash, request.SupervisorUsername, cancellationToken);
            if (sup == null)
                return Forbid();
            supervisorUsername = sup.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            supervisorUsername = GetAuthenticatedUsername();
        }
        else
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(supervisorUsername))
            return Unauthorized();

        var terminal = await _context.Terminals.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, cancellationToken);
        if (terminal == null)
            return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
            return Forbid();
        if (string.Equals(GetAuthenticatedRole(), "Terminal", StringComparison.OrdinalIgnoreCase) &&
            User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString())
            return Forbid();

        string opId = string.IsNullOrWhiteSpace(request.ClientOperationId)
            ? $"OVN-CLOSE-{Guid.NewGuid():N}"
            : request.ClientOperationId.Trim();
        if (opId.Length > 100)
            return BadRequest("ClientOperationId exceeds maximum length.");

        string cleanReason = request.Reason.Trim();
        if (cleanReason.Length > 500) cleanReason = cleanReason[..500];

        using var tx = await _context.Database.BeginTransactionAsync(
            _context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(_context, request.TerminalId, cancellationToken);

            // Idempotency guard: replay with identical operation ID returns the already-resolved shift cleanly
            var existingByOp = await _context.CashDrawerShifts
                .FirstOrDefaultAsync(s => s.RecoveryOperationId == opId || s.ClientCloseId == opId, cancellationToken);
            if (existingByOp != null)
            {
                if (existingByOp.TerminalId != request.TerminalId)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Conflict("Recovery operation ID already belongs to a different terminal shift.");
                }
                if (existingByOp.Status == "CLOSED" &&
                    string.Equals(existingByOp.ClosingType, TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    var replaySnap = await ReadSnapshotAsync(_context, existingByOp.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
                    return Ok(replaySnap!);
                }
            }

            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
            {
                shift = await _context.CashDrawerShifts.Include(s => s.Movements)
                    .FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId && s.TerminalId == request.TerminalId, cancellationToken);
            }
            if (shift == null && !string.IsNullOrWhiteSpace(request.ClientShiftId))
            {
                string cid = request.ClientShiftId.Trim();
                shift = await _context.CashDrawerShifts.Include(s => s.Movements)
                    .FirstOrDefaultAsync(s => s.ClientShiftId == cid && s.TerminalId == request.TerminalId, cancellationToken);
            }
            if (shift == null)
            {
                shift = await _context.CashDrawerShifts.Include(s => s.Movements)
                    .Where(s => s.TerminalId == request.TerminalId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .ThenByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (shift == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return NotFound("No matching unresolved shift found for this terminal.");
            }
            if (shift.Status != "OPEN")
            {
                await tx.RollbackAsync(cancellationToken);
                return Conflict("This cashier shift has already been closed.");
            }

            var closedAtUtc = request.ClosedAtUtc.HasValue && NormalizeUtc(request.ClosedAtUtc.Value) >= NormalizeUtc(shift.OpenedAt)
                ? NormalizeUtc(request.ClosedAtUtc.Value)
                : BusinessDayClock.UtcNow();

            string originalBusinessDate = BusinessDayClock.GetShiftBusinessDateString(shift.OpenedAt, shift.BusinessDate);
            if (!BusinessDayClock.IsPreviousBusinessDateShift(shift.OpenedAt, closedAtUtc, shift.BusinessDate))
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Supervisor overnight recovery only applies to open shifts from a previous Business Date.");
            }

            int pendingCount = shift.Movements.Count(m => m.Status == "PENDING_APPROVAL");
            if (pendingCount > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest($"Cannot close shift while {pendingCount} cash movement(s) are still pending supervisor approval. Please approve or reject them first.");
            }

            RecalculateShiftExpectedBalance(shift);
            shift.OutletId ??= terminal.OutletId;
            shift.BusinessDate = originalBusinessDate; // Strictly preserve original Business Date
            shift.ActualCountedBalance = countedCash;
            shift.Discrepancy = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

            var reconciliations = await BuildPaymentReconciliationsAsync(_context, shift, request.PaymentDeclarations, cancellationToken);

            shift.Status = "CLOSED";
            shift.ClosedAt = closedAtUtc;
            shift.ClosingType = TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery;
            shift.ClosedBySupervisorId = supervisorUsername;
            shift.RecoveryReason = cleanReason;
            shift.RecoveryStatus = TerminalClosingLockDto.RecoveryStatusResolved;
            shift.RecoveryOperationId = opId;
            shift.ClientCloseId = opId;
            shift.ClosingDenominationsJson = JsonSerializer.Serialize(validatedDenominations);
            shift.ClosingPaymentReconciliationsJson = JsonSerializer.Serialize(reconciliations);

            string composedNote = string.IsNullOrWhiteSpace(request.Notes)
                ? $"[Overnight Recovery by {supervisorUsername}] {cleanReason}"
                : $"[Overnight Recovery by {supervisorUsername}] {cleanReason} | {request.Notes.Trim()}";
            shift.Notes = composedNote.Length > 255 ? composedNote[..255] : composedNote;

            if (string.IsNullOrWhiteSpace(shift.ClosingOriginalDeclarationJson))
            {
                var origDecls = reconciliations
                    .Where(r => !r.IsCash)
                    .Select(r => new PaymentMethodDeclarationDto
                    {
                        PaymentMethodCode = r.PaymentMethodCode,
                        PaymentMethodName = r.PaymentMethodName,
                        IsActive = r.IsActive,
                        DisplayOrder = r.DisplayOrder,
                        DeclaredAmount = r.DeclaredAmount
                    })
                    .ToList();
                var initialDeclaration = new CashierDeclarationReportDto
                {
                    ShiftId = shift.ShiftId,
                    ClientShiftId = shift.ClientShiftId,
                    BranchId = shift.BranchId,
                    OutletId = shift.OutletId ?? terminal.OutletId,
                    TerminalId = shift.TerminalId,
                    CashierId = shift.CashierId,
                    OpenedAt = shift.OpenedAt,
                    ClosedAt = shift.ClosedAt,
                    SubmittedAt = closedAtUtc,
                    TotalCountedCash = countedCash,
                    TotalNonCashDeclared = origDecls.Sum(d => d.DeclaredAmount),
                    HasRecordedDenominations = validatedDenominations.Count > 0,
                    Denominations = validatedDenominations,
                    PaymentDeclarations = origDecls,
                    Notes = shift.Notes
                };
                shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(initialDeclaration);
            }

            // Restrict original cashier ONLY for the ORIGINAL Business Date; never lock the terminal for the new Business Date
            await POS.Api.Services.TerminalClosingLockGuard.RestrictCashierOnCloseAsync(
                _context,
                shift.BranchId,
                shift.TerminalId,
                shift.CashierId,
                shift.ShiftId,
                shift.ClientShiftId,
                shift.ClientCloseId,
                closedAtUtc,
                cancellationToken,
                overrideBusinessDate: originalBusinessDate);

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var closedDto = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
            return Ok(closedDto!);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to complete supervisor overnight recovery closing: {ex.Message}");
        }
    }

    /// <summary>
    /// Records that physical drawer cash for an unresolved previous-Business-Date shift cannot be verified.
    /// Keeps the shift strictly OPEN/unresolved for investigation: never fabricates a cash count,
    /// never marks balanced/closed, never resets the drawer, and never releases terminal ownership.
    /// </summary>
    [HttpPost("overnight-recovery-unverified")]
    public async Task<ActionResult<CashDrawerShiftDto>> FlagUnverifiedCash(
        [FromBody] SupervisorUnverifiedCashRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (request == null || request.TerminalId <= 0)
            return BadRequest("Valid terminalId is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest("A reason is required to flag unverified cash for investigation.");

        await EnsureOvernightRecoverySchemaAsync(_context, cancellationToken);

        string? supervisorUsername = null;
        if (!string.IsNullOrWhiteSpace(request.SupervisorPin) || !string.IsNullOrWhiteSpace(request.SupervisorPinHash))
        {
            var sup = await VerifySupervisorPinOrHashAsync(request.SupervisorPin, request.SupervisorPinHash, request.SupervisorUsername, cancellationToken);
            if (sup == null)
                return Forbid();
            supervisorUsername = sup.Username;
        }
        else if (IsSupervisorRole(GetAuthenticatedRole()))
        {
            supervisorUsername = GetAuthenticatedUsername();
        }
        else
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(supervisorUsername))
            return Unauthorized();

        var terminal = await _context.Terminals.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId, cancellationToken);
        if (terminal == null)
            return NotFound("Terminal not found.");

        if (!User.IsInRole("Admin") &&
            int.TryParse(User.FindFirst("branch_id")?.Value, out int claimBranchId) &&
            terminal.BranchId != claimBranchId)
            return Forbid();
        if (string.Equals(GetAuthenticatedRole(), "Terminal", StringComparison.OrdinalIgnoreCase) &&
            User.FindFirst("terminal_id")?.Value != request.TerminalId.ToString())
            return Forbid();

        string opId = string.IsNullOrWhiteSpace(request.ClientOperationId)
            ? $"OVN-UNVER-{Guid.NewGuid():N}"
            : request.ClientOperationId.Trim();
        if (opId.Length > 100)
            return BadRequest("ClientOperationId exceeds maximum length.");

        string cleanReason = request.Reason.Trim();
        if (cleanReason.Length > 500) cleanReason = cleanReason[..500];

        using var tx = await _context.Database.BeginTransactionAsync(
            _context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(_context, request.TerminalId, cancellationToken);

            var existingByOp = await _context.CashDrawerShifts
                .FirstOrDefaultAsync(s => s.RecoveryOperationId == opId, cancellationToken);
            if (existingByOp != null)
            {
                if (existingByOp.TerminalId != request.TerminalId)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Conflict("Operation ID already belongs to a different terminal shift.");
                }
                await tx.RollbackAsync(cancellationToken);
                var existingSnap = await ReadSnapshotAsync(_context, existingByOp.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
                return Ok(existingSnap!);
            }

            CashDrawerShift? shift = null;
            if (request.ShiftId > 0)
                shift = await _context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId && s.TerminalId == request.TerminalId, cancellationToken);
            if (shift == null && !string.IsNullOrWhiteSpace(request.ClientShiftId))
            {
                string cid = request.ClientShiftId.Trim();
                shift = await _context.CashDrawerShifts.FirstOrDefaultAsync(s => s.ClientShiftId == cid && s.TerminalId == request.TerminalId, cancellationToken);
            }
            if (shift == null)
            {
                shift = await _context.CashDrawerShifts
                    .Where(s => s.TerminalId == request.TerminalId && s.Status == "OPEN")
                    .OrderByDescending(s => s.OpenedAt)
                    .ThenByDescending(s => s.ShiftId)
                    .FirstOrDefaultAsync(cancellationToken);
            }

            if (shift == null)
            {
                await tx.RollbackAsync(cancellationToken);
                return NotFound("No matching unresolved shift found for this terminal.");
            }

            // If shift was already validly resolved by supervisor later (out-of-order replay), never revert a closed shift
            if (shift.Status == "CLOSED")
            {
                await tx.RollbackAsync(cancellationToken);
                var closedSnap = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
                return Ok(closedSnap!);
            }

            var reportedAtUtc = request.ReportedAtUtc.HasValue ? NormalizeUtc(request.ReportedAtUtc.Value) : BusinessDayClock.UtcNow();
            if (!BusinessDayClock.IsPreviousBusinessDateShift(shift.OpenedAt, reportedAtUtc, shift.BusinessDate))
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Unverified cash investigation flag only applies to open shifts from a previous Business Date.");
            }

            shift.OutletId ??= terminal.OutletId;
            shift.BusinessDate ??= BusinessDayClock.GetShiftBusinessDateString(shift.OpenedAt, shift.BusinessDate);
            shift.RecoveryStatus = TerminalClosingLockDto.RecoveryStatusUnverifiedCash;
            shift.UnverifiedCashReason = cleanReason;
            shift.UnverifiedCashReportedBy = supervisorUsername;
            shift.UnverifiedCashReportedAtUtc = reportedAtUtc;
            shift.RecoveryOperationId = opId;
            // Strictly keep Status = "OPEN", ActualCountedBalance = null, Discrepancy = null, ClosedAt = null!

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var flaggedSnap = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: true, isReprint: false);
            return Ok(flaggedSnap!);
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to record unverified cash investigation: {ex.Message}");
        }
    }

    [HttpGet("last-closed/{terminalId:int}")]
    public async Task<ActionResult<CashDrawerShiftDto?>> GetLastClosedShift(
        int terminalId,
        [FromQuery] string? supervisorPin = null,
        CancellationToken cancellationToken = default)
    {
        bool isSupervisor = IsSupervisorRole(GetAuthenticatedRole());
        if (!isSupervisor && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifySupervisorPinAsync(supervisorPin, cancellationToken);
            isSupervisor = sup != null;
        }
        if (!isSupervisor && User.FindFirst("terminal_id")?.Value != terminalId.ToString()) return Forbid();
        var lastClosed = await _context.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "CLOSED")
            .OrderByDescending(s => s.ClosedAt)
            .ThenByDescending(s => s.ShiftId)
            .Select(s => (long?)s.ShiftId)
            .FirstOrDefaultAsync(cancellationToken);
        if (!lastClosed.HasValue) return Ok((CashDrawerShiftDto?)null);
        var snapshot = await ReadSnapshotAsync(_context, lastClosed.Value, cancellationToken, requireOpen: false, isReprint: true);
        return Ok(isSupervisor ? snapshot : RedactForCashier(snapshot));
    }

    [HttpPost("close-shift")]
    public async Task<IActionResult> CloseShift([FromBody] CloseShiftDto request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            return BadRequest("Shift closing request is required.");

        bool hasDenominations = request.Denominations != null && request.Denominations.Count > 0;
        bool hasDeclarations = request.PaymentDeclarations != null && request.PaymentDeclarations.Count > 0;
        bool hasStructuredReconciliation = hasDenominations || hasDeclarations;
        if (hasStructuredReconciliation && !request.IsDeclarationConfirmed)
            return BadRequest("Cashier must explicitly review and confirm the closing declaration before submitting.");

        decimal countedCash;
        List<CashDenominationLineDto>? validatedDenominations = null;
        if (hasDenominations)
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.Denominations, out var normDenoms, out countedCash, out var denomError))
                return BadRequest(denomError);
            validatedDenominations = normDenoms;
        }
        else
        {
            if (request.ActualCountedBalance < 0m)
                return BadRequest("Actual counted cash balance cannot be negative.");
            if (decimal.Round(request.ActualCountedBalance, 2) != request.ActualCountedBalance)
                return BadRequest("Actual counted cash balance supports at most two decimal places.");
            countedCash = Math.Round(request.ActualCountedBalance, 2, MidpointRounding.AwayFromZero);
        }

        if (request.PaymentDeclarations != null)
        {
            var seenMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decl in request.PaymentDeclarations)
            {
                if (decl == null) continue;
                string code = (decl.PaymentMethodCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code))
                    return BadRequest("Payment method code is required for all payment declarations.");
                if (code == "CASH") continue;
                if (!seenMethods.Add(code))
                    return BadRequest($"Duplicate payment declaration for {code}.");
                if (decl.DeclaredAmount < 0m)
                    return BadRequest($"Cashier-declared total for {code} cannot be negative.");
                if (decimal.Round(decl.DeclaredAmount, 2) != decl.DeclaredAmount)
                    return BadRequest($"Cashier-declared total for {code} supports at most two decimal places.");
            }
        }

        using var tx = await _context.Database.BeginTransactionAsync(_context.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
        try
        {
            var shiftLookupQuery = _context.CashDrawerShifts.AsQueryable();
            if (request.ShiftId > 0)
                shiftLookupQuery = shiftLookupQuery.Where(s => s.ShiftId == request.ShiftId);
            else if (!string.IsNullOrWhiteSpace(request.ClientShiftId))
                shiftLookupQuery = shiftLookupQuery.Where(s => s.ClientShiftId == request.ClientShiftId);
            else
            {
                await tx.RollbackAsync(cancellationToken);
                return BadRequest("Invalid or already closed shift.");
            }

            var terminalId = await shiftLookupQuery.Select(s => s.TerminalId).FirstOrDefaultAsync(cancellationToken);
            await POS.Api.Services.ShiftLifecycle.LockTerminalAsync(_context, terminalId, cancellationToken);

            var shift = await shiftLookupQuery
                .Include(s => s.Movements)
                .FirstOrDefaultAsync(cancellationToken);

            var caller = GetAuthenticatedUsername();
            bool isTerminalTokenForShiftOwner = string.Equals(GetAuthenticatedRole(), "Terminal", StringComparison.OrdinalIgnoreCase)
                && shift != null
                && User.FindFirst("terminal_id")?.Value == shift.TerminalId.ToString()
                && !string.IsNullOrWhiteSpace(request.CashierId)
                && string.Equals(request.CashierId, shift.CashierId, StringComparison.Ordinal);
            if (shift != null && (User.FindFirst("terminal_id")?.Value != shift.TerminalId.ToString() || (!string.Equals(shift.CashierId, caller, StringComparison.OrdinalIgnoreCase) && !isTerminalTokenForShiftOwner)))
                return Forbid();
            if (shift != null && !string.IsNullOrWhiteSpace(request.CashierId) && !string.Equals(request.CashierId, shift.CashierId, StringComparison.Ordinal))
                return Forbid();

            if (shift != null && shift.Status == "CLOSED" && !string.IsNullOrWhiteSpace(request.ClientCloseId)
                && string.Equals(shift.ClientCloseId, request.ClientCloseId.Trim(), StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                var existingClosed = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
                return Ok(IsSupervisorRole(GetAuthenticatedRole()) ? existingClosed : RedactForCashier(existingClosed));
            }

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

            var effectiveCloseUtc = request.ClosedAt.HasValue && request.ClosedAt.Value >= shift.OpenedAt
                ? NormalizeUtc(request.ClosedAt.Value)
                : BusinessDayClock.UtcNow();
            string origShiftBizDate = BusinessDayClock.GetShiftBusinessDateString(shift.OpenedAt, shift.BusinessDate);
            if (BusinessDayClock.IsPreviousBusinessDateShift(shift.OpenedAt, effectiveCloseUtc, shift.BusinessDate))
            {
                await tx.RollbackAsync(cancellationToken);
                return Conflict(TerminalClosingLockDto.FormatUnresolvedPreviousDayShiftMessage(
                    origShiftBizDate, shift.CashierId, shift.RecoveryStatus, shift.UnverifiedCashReason));
            }

            RecalculateShiftExpectedBalance(shift);
            shift.ActualCountedBalance = countedCash;
            shift.Discrepancy = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

            var reconciliations = await BuildPaymentReconciliationsAsync(_context, shift, request.PaymentDeclarations, cancellationToken);

            shift.Status = "CLOSED";
            shift.ClosedAt = effectiveCloseUtc;
            shift.BusinessDate ??= origShiftBizDate;
            var trimmedNotes = request.Notes?.Trim();
            shift.Notes = string.IsNullOrWhiteSpace(trimmedNotes)
                ? null
                : (trimmedNotes.Length > 255 ? trimmedNotes[..255] : trimmedNotes);
            if (!string.IsNullOrWhiteSpace(request.ClientCloseId))
                shift.ClientCloseId = request.ClientCloseId.Trim();
            shift.ClosingDenominationsJson = validatedDenominations != null ? JsonSerializer.Serialize(validatedDenominations) : null;
            shift.ClosingPaymentReconciliationsJson = JsonSerializer.Serialize(reconciliations);

            if (string.IsNullOrWhiteSpace(shift.ClosingOriginalDeclarationJson))
            {
                var origDecls = reconciliations
                    .Where(r => !r.IsCash)
                    .Select(r => new PaymentMethodDeclarationDto
                    {
                        PaymentMethodCode = r.PaymentMethodCode,
                        PaymentMethodName = r.PaymentMethodName,
                        IsActive = r.IsActive,
                        DisplayOrder = r.DisplayOrder,
                        DeclaredAmount = r.DeclaredAmount
                    })
                    .ToList();
                var initialDeclaration = new CashierDeclarationReportDto
                {
                    ShiftId = shift.ShiftId,
                    ClientShiftId = shift.ClientShiftId,
                    BranchId = shift.BranchId,
                    TerminalId = shift.TerminalId,
                    CashierId = shift.CashierId,
                    OpenedAt = shift.OpenedAt,
                    ClosedAt = shift.ClosedAt,
                    SubmittedAt = shift.ClosedAt ?? BusinessDayClock.UtcNow(),
                    TotalCountedCash = countedCash,
                    TotalNonCashDeclared = origDecls.Sum(d => d.DeclaredAmount),
                    HasRecordedDenominations = validatedDenominations != null && validatedDenominations.Count > 0,
                    Denominations = validatedDenominations ?? new(),
                    PaymentDeclarations = origDecls,
                    Notes = shift.Notes
                };
                shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(initialDeclaration);
            }

            await POS.Api.Services.TerminalClosingLockGuard.RestrictCashierOnCloseAsync(
                _context,
                shift.BranchId,
                shift.TerminalId,
                shift.CashierId,
                shift.ShiftId,
                shift.ClientShiftId,
                shift.ClientCloseId,
                shift.ClosedAt ?? BusinessDayClock.UtcNow(),
                cancellationToken);

            await _context.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var closedDto = await ReadSnapshotAsync(_context, shift.ShiftId, cancellationToken, requireOpen: false, isReprint: false);
            return Ok(IsSupervisorRole(GetAuthenticatedRole()) ? closedDto : RedactForCashier(closedDto));
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return StatusCode(500, $"Failed to close shift: {ex.Message}");
        }
    }

    public static async Task<List<PaymentMethodReconciliationDto>> BuildPaymentReconciliationsAsync(
        PosDbContext context,
        CashDrawerShift shift,
        IReadOnlyList<PaymentMethodDeclarationDto>? declarations = null,
        CancellationToken cancellationToken = default)
    {
        var configuredMethods = await context.PaymentMethods
            .AsNoTracking()
            .OrderBy(pm => pm.DisplayOrder)
            .ThenBy(pm => pm.Name)
            .ToListAsync(cancellationToken);

        var shiftOrders = await context.Orders
            .AsNoTracking()
            .Include(o => o.Payments)
            .Where(o => o.ShiftId == shift.ShiftId)
            .ToListAsync(cancellationToken);

        var shiftOrderIds = shiftOrders.Select(o => o.OrderId).ToList();
        var returnRefunds = shiftOrderIds.Count > 0
            ? await context.Refunds
                .AsNoTracking()
                .Include(r => r.OriginalPayment)
                .Where(r => r.VoidId == null && r.Status == "COMPLETED" && r.OriginalPayment != null && shiftOrderIds.Contains(r.OriginalPayment.OrderId))
                .ToListAsync(cancellationToken)
            : new List<Refund>();

        var list = new List<PaymentMethodReconciliationDto>();

        decimal cashGross = shift.TotalCashSales;
        decimal cashRefunds = shift.TotalCashRefunds;
        decimal cashNetSales = Math.Round(cashGross - cashRefunds, 2, MidpointRounding.AwayFromZero);
        decimal cashExpected = shift.ExpectedBalance;
        decimal cashDeclared = shift.ActualCountedBalance ?? 0m;
        decimal cashVariance = Math.Round(cashDeclared - cashExpected, 2, MidpointRounding.AwayFromZero);

        list.Add(new PaymentMethodReconciliationDto
        {
            PaymentMethodCode = "CASH",
            PaymentMethodName = "Cash (Drawer)",
            IsCash = true,
            IsActive = true,
            DisplayOrder = 0,
            GrossSalesAmount = cashGross,
            RefundAmount = cashRefunds,
            NetSalesAmount = cashNetSales,
            ExpectedAmount = cashExpected,
            DeclaredAmount = cashDeclared,
            Variance = cashVariance,
            Status = PaymentMethodReconciliationDto.ComputeStatus(cashVariance)
        });

        var nonCashCodes = new List<string>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CASH" };

        foreach (var pm in configuredMethods.Where(m => m.IsActive))
        {
            if (seenCodes.Add(pm.Code)) nonCashCodes.Add(pm.Code);
        }

        foreach (var p in shiftOrders.SelectMany(o => o.Payments))
        {
            if (!string.IsNullOrWhiteSpace(p.PaymentMethod)
                && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded)
                && seenCodes.Add(p.PaymentMethod.Trim()))
            {
                nonCashCodes.Add(p.PaymentMethod.Trim().ToUpperInvariant());
            }
        }

        foreach (var r in returnRefunds)
        {
            if (!string.IsNullOrWhiteSpace(r.RefundMethod) && seenCodes.Add(r.RefundMethod.Trim()))
                nonCashCodes.Add(r.RefundMethod.Trim().ToUpperInvariant());
        }

        if (declarations != null)
        {
            foreach (var d in declarations)
            {
                if (d != null && !string.IsNullOrWhiteSpace(d.PaymentMethodCode) && seenCodes.Add(d.PaymentMethodCode.Trim()))
                    nonCashCodes.Add(d.PaymentMethodCode.Trim().ToUpperInvariant());
            }
        }

        foreach (var code in nonCashCodes)
        {
            var cfg = configuredMethods.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
            decimal gross = shiftOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                            && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded))
                .Sum(p => p.Amount);
            decimal voidRefund = shiftOrders.SelectMany(o => o.Payments)
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                            && p.PaymentStatus == "REFUNDED")
                .Sum(p => p.Amount);
            decimal retRefund = returnRefunds
                .Where(r => string.Equals(r.RefundMethod, code, StringComparison.OrdinalIgnoreCase))
                .Sum(r => r.RefundAmount);
            decimal totalRefund = Math.Round(voidRefund + retRefund, 2, MidpointRounding.AwayFromZero);
            decimal netSales = Math.Round(gross - totalRefund, 2, MidpointRounding.AwayFromZero);
            decimal expected = netSales;

            var declEntry = declarations?.FirstOrDefault(d => d != null && string.Equals(d.PaymentMethodCode?.Trim(), code, StringComparison.OrdinalIgnoreCase));
            decimal declared = Math.Round(declEntry?.DeclaredAmount ?? 0m, 2, MidpointRounding.AwayFromZero);
            decimal variance = Math.Round(declared - expected, 2, MidpointRounding.AwayFromZero);

            list.Add(new PaymentMethodReconciliationDto
            {
                PaymentMethodCode = cfg?.Code ?? code.ToUpperInvariant(),
                PaymentMethodName = cfg?.Name ?? declEntry?.PaymentMethodName ?? code.ToUpperInvariant(),
                IsCash = false,
                IsActive = cfg?.IsActive ?? false,
                DisplayOrder = cfg?.DisplayOrder ?? 999,
                GrossSalesAmount = gross,
                RefundAmount = totalRefund,
                NetSalesAmount = netSales,
                ExpectedAmount = expected,
                DeclaredAmount = declared,
                Variance = variance,
                Status = PaymentMethodReconciliationDto.ComputeStatus(variance)
            });
        }

        return list.OrderBy(r => r.IsCash ? 0 : 1).ThenBy(r => r.DisplayOrder).ThenBy(r => r.PaymentMethodName).ToList();
    }

    // One SQL statement supplies both totals and accepted IDs. Reading IDs separately can
    // race an offline upload commit and either double-count or omit that sale locally.
    public static async Task<CashDrawerShiftDto?> ReadSnapshotAsync(
        PosDbContext context,
        long shiftId,
        CancellationToken cancellationToken = default,
        bool requireOpen = false,
        bool isReprint = false)
    {
        var snapshot = await context.CashDrawerShifts.AsNoTracking().AsSingleQuery()
            .Include(s => s.Movements)
            .Where(s => s.ShiftId == shiftId && (!requireOpen || s.Status == "OPEN"))
            .Select(s => new
            {
                Shift = s,
                Accepted = (from acknowledgement in context.OfflineSyncIdempotencyRecords
                            join order in context.Orders on acknowledgement.ServerOrderId equals order.OrderId
                            where order.ShiftId == s.ShiftId
                            select acknowledgement.LocalTransactionId).ToList()
            }).FirstOrDefaultAsync(cancellationToken);
        if (snapshot == null) return null;
        var dto = MapShiftToDto(snapshot.Shift);
        dto.AcknowledgedLocalTransactionIds = snapshot.Accepted;
        await PopulateShiftReconciliationAndXReadingAsync(context, snapshot.Shift, dto, isReprint, cancellationToken);
        return dto;
    }

    public static async Task PopulateShiftReconciliationAndXReadingAsync(
        PosDbContext context,
        CashDrawerShift shift,
        CashDrawerShiftDto dto,
        bool isReprint = false,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(shift.ClosingDenominationsJson))
        {
            try
            {
                dto.Denominations = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(shift.ClosingDenominationsJson) ?? new();
                dto.HasRecordedDenominations = dto.Denominations.Count > 0;
            }
            catch
            {
                dto.Denominations = new();
                dto.HasRecordedDenominations = false;
            }
        }
        else
        {
            dto.Denominations = new();
            dto.HasRecordedDenominations = false;
        }

        List<PaymentMethodReconciliationDto>? savedReconciliations = null;
        if (!string.IsNullOrWhiteSpace(shift.ClosingPaymentReconciliationsJson))
        {
            try
            {
                savedReconciliations = JsonSerializer.Deserialize<List<PaymentMethodReconciliationDto>>(shift.ClosingPaymentReconciliationsJson);
            }
            catch { savedReconciliations = null; }
        }

        List<PaymentMethodDeclarationDto>? savedDeclarations = null;
        if (savedReconciliations != null && savedReconciliations.Count > 0)
        {
            savedDeclarations = savedReconciliations
                .Where(r => !r.IsCash)
                .Select(r => new PaymentMethodDeclarationDto
                {
                    PaymentMethodCode = r.PaymentMethodCode,
                    PaymentMethodName = r.PaymentMethodName,
                    IsActive = r.IsActive,
                    DisplayOrder = r.DisplayOrder,
                    DeclaredAmount = r.DeclaredAmount
                })
                .ToList();
        }

        dto.PaymentReconciliations = await BuildPaymentReconciliationsAsync(context, shift, savedDeclarations, cancellationToken);

        dto.TotalNonCashExpected = dto.PaymentReconciliations.Where(r => !r.IsCash).Sum(r => r.ExpectedAmount);
        dto.TotalNonCashDeclared = dto.PaymentReconciliations.Where(r => !r.IsCash).Sum(r => r.DeclaredAmount);
        dto.TotalNetSales = Math.Round((shift.TotalCashSales - shift.TotalCashRefunds) + dto.TotalNonCashExpected, 2, MidpointRounding.AwayFromZero);
        dto.ConfiguredNonCashMethods = dto.PaymentReconciliations
            .Where(r => !r.IsCash)
            .Select(r => new PaymentMethodDeclarationDto
            {
                PaymentMethodCode = r.PaymentMethodCode,
                PaymentMethodName = r.PaymentMethodName,
                IsActive = r.IsActive,
                DisplayOrder = r.DisplayOrder,
                DeclaredAmount = r.DeclaredAmount
            })
            .ToList();

        if (!string.IsNullOrWhiteSpace(shift.ClosingOriginalDeclarationJson))
        {
            try
            {
                dto.OriginalDeclaration = JsonSerializer.Deserialize<CashierDeclarationReportDto>(shift.ClosingOriginalDeclarationJson);
                if (dto.OriginalDeclaration != null)
                {
                    dto.OriginalDeclaration.HasRecordedDenominations = dto.OriginalDeclaration.Denominations.Count > 0
                        && (!string.IsNullOrWhiteSpace(shift.ClosingDenominationsJson) || dto.OriginalDeclaration.Denominations.Any(d => d.Quantity > 0));
                    if (!dto.OriginalDeclaration.HasRecordedDenominations && string.IsNullOrWhiteSpace(shift.ClosingDenominationsJson))
                        dto.OriginalDeclaration.Denominations = new();
                }
            }
            catch { dto.OriginalDeclaration = null; }
        }
        if (!string.IsNullOrWhiteSpace(shift.ClosingRecountHistoryJson))
        {
            try { dto.RecountHistory = JsonSerializer.Deserialize<List<ShiftRecountAuditDto>>(shift.ClosingRecountHistoryJson) ?? new(); }
            catch { dto.RecountHistory = new(); }
        }

        var branch = await context.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.BranchId == shift.BranchId, cancellationToken);
        var terminal = shift.TerminalId > 0
            ? await context.Terminals.AsNoTracking().Include(t => t.Outlet).FirstOrDefaultAsync(t => t.TerminalId == shift.TerminalId, cancellationToken)
            : null;

        string branchName = branch?.Name ?? (shift.BranchId > 0 ? $"Branch #{shift.BranchId}" : "Unknown Branch");
        int outletId = terminal?.OutletId ?? 0;
        string outletName = terminal?.Outlet?.Name ?? (outletId > 0 ? $"Outlet #{outletId}" : "Unknown / Legacy Outlet");
        string terminalCode = terminal?.TerminalCode ?? (shift.TerminalId > 0 ? $"LEGACY-{shift.TerminalId}" : "LEGACY");
        string terminalName = terminal?.Name ?? (shift.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{shift.TerminalId})" : "Unknown / Legacy Terminal");

        dto.BranchName = branchName;
        dto.OutletId = outletId;
        dto.OutletName = outletName;
        dto.TerminalCode = terminalCode;
        dto.TerminalName = terminalName;

        foreach (var mv in dto.Movements)
        {
            mv.BranchName = branchName;
            mv.OutletId = outletId;
            mv.OutletName = outletName;
            mv.TerminalCode = terminalCode;
            mv.TerminalName = terminalName;
        }

        if (dto.OriginalDeclaration != null)
        {
            if (string.IsNullOrWhiteSpace(dto.OriginalDeclaration.BranchName)) dto.OriginalDeclaration.BranchName = branchName;
            if (dto.OriginalDeclaration.OutletId <= 0) dto.OriginalDeclaration.OutletId = outletId;
            if (string.IsNullOrWhiteSpace(dto.OriginalDeclaration.OutletName)) dto.OriginalDeclaration.OutletName = outletName;
            if (string.IsNullOrWhiteSpace(dto.OriginalDeclaration.TerminalCode)) dto.OriginalDeclaration.TerminalCode = terminalCode;
            if (string.IsNullOrWhiteSpace(dto.OriginalDeclaration.TerminalName)) dto.OriginalDeclaration.TerminalName = terminalName;
        }

        decimal countedCash = shift.ActualCountedBalance ?? dto.Denominations.Sum(d => d.Subtotal);
        dto.CashierDeclaration = new CashierDeclarationReportDto
        {
            ShiftId = shift.ShiftId,
            ClientShiftId = shift.ClientShiftId,
            BranchId = shift.BranchId,
            BranchName = branchName,
            BranchCode = branch?.Code,
            BranchAddress = branch?.Address,
            BranchPhone = branch?.Phone,
            OutletId = outletId,
            OutletName = outletName,
            TerminalId = shift.TerminalId,
            TerminalCode = terminalCode,
            TerminalName = terminalName,
            PTUNumber = terminal?.Ptunumber,
            MINNumber = terminal?.Minnumber,
            SerialNumber = terminal?.SerialNumber,
            CashierId = shift.CashierId,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            SubmittedAt = shift.ClosedAt ?? DateTime.UtcNow,
            IsReprint = isReprint,
            TotalCountedCash = countedCash,
            TotalNonCashDeclared = dto.TotalNonCashDeclared,
            HasRecordedDenominations = dto.HasRecordedDenominations,
            Denominations = dto.Denominations,
            PaymentDeclarations = dto.ConfiguredNonCashMethods,
            Notes = shift.Notes
        };

        var shiftOrders = await context.Orders.AsNoTracking()
            .Where(o => o.ShiftId == shift.ShiftId)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        var shiftOrderIds = shiftOrders.Select(o => o.OrderId).ToList();
        decimal shiftReturnRefunds = shiftOrderIds.Count > 0
            ? await context.Refunds.AsNoTracking()
                .Include(r => r.OriginalPayment)
                .Where(r => r.VoidId == null && r.Status == "COMPLETED" && r.OriginalPayment != null && shiftOrderIds.Contains(r.OriginalPayment.OrderId))
                .SumAsync(r => (decimal?)r.RefundAmount, cancellationToken) ?? 0m
            : 0m;

        var periodEndUtc = BusinessDayClock.NormalizeUtc(shift.ClosedAt ?? BusinessDayClock.UtcNow());
        var shiftOpenedUtc = BusinessDayClock.NormalizeUtc(shift.OpenedAt);
        bool isOvernightRecovery = string.Equals(shift.ClosingType, TerminalClosingLockDto.ClosingTypeSupervisorOvernightRecovery, StringComparison.OrdinalIgnoreCase);
        var businessDate = isOvernightRecovery
            ? BusinessDayClock.GetShiftBusinessDate(shift.OpenedAt, shift.BusinessDate)
            : BusinessDayClock.GetBusinessDate(periodEndUtc);
        var (businessDayStartUtc, businessDayEndUtc) = BusinessDayClock.GetPeriodUtc(businessDate);
        if (isOvernightRecovery && periodEndUtc > businessDayEndUtc)
            periodEndUtc = businessDayEndUtc;
        var periodEnd = DateTime.SpecifyKind(periodEndUtc, DateTimeKind.Unspecified);
        var businessDayStart = DateTime.SpecifyKind(businessDayStartUtc, DateTimeKind.Unspecified);
        var shiftOpenedUnspecified = DateTime.SpecifyKind(shiftOpenedUtc, DateTimeKind.Unspecified);
        var reportStart = shiftOpenedUnspecified < businessDayStart ? shiftOpenedUnspecified : businessDayStart;

        var periodOrders = await context.Orders.AsNoTracking()
            .Where(o => o.TerminalId == shift.TerminalId && o.CreatedAt >= reportStart && o.CreatedAt <= periodEnd)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var so in shiftOrders)
        {
            if (periodOrders.All(po => po.OrderId != so.OrderId))
                periodOrders.Add(so);
        }

        var periodOrderIds = periodOrders.Select(o => o.OrderId).ToList();
        decimal periodReturnRefunds = periodOrderIds.Count > 0
            ? await context.Refunds.AsNoTracking()
                .Include(r => r.OriginalPayment)
                .Where(r => r.VoidId == null && r.Status == "COMPLETED" && r.OriginalPayment != null && periodOrderIds.Contains(r.OriginalPayment.OrderId))
                .SumAsync(r => (decimal?)r.RefundAmount, cancellationToken) ?? 0m
            : 0m;

        int periodReturnCount = periodOrderIds.Count > 0
            ? await context.SalesReturns.AsNoTracking()
                .CountAsync(sr => periodOrderIds.Contains(sr.OriginalOrderId) && sr.Status == "COMPLETED", cancellationToken)
            : 0;

        var paidPeriodOrders = periodOrders.Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "VOIDED").ToList();
        var voidPeriodOrders = periodOrders.Where(o => o.PaymentStatus == "VOIDED" || o.OrderStatus == "VOIDED").ToList();

        var paidShiftOrders = shiftOrders.Where(o => o.PaymentStatus == "PAID" && o.OrderStatus != "VOIDED").ToList();
        var voidShiftOrders = shiftOrders.Where(o => o.PaymentStatus == "VOIDED" || o.OrderStatus == "VOIDED").ToList();

        decimal periodGross = paidPeriodOrders.Sum(o => o.Subtotal) + voidPeriodOrders.Sum(o => o.Subtotal);
        decimal periodDiscounts = paidPeriodOrders.Sum(o => o.DiscountAmount);
        decimal periodVoidTotal = voidPeriodOrders.Sum(o => o.TotalAmount);
        decimal periodRefundsAndVoids = Math.Round(periodVoidTotal + periodReturnRefunds, 2, MidpointRounding.AwayFromZero);
        decimal periodNetSales = Math.Round(paidPeriodOrders.Sum(o => o.TotalAmount) - periodReturnRefunds, 2, MidpointRounding.AwayFromZero);

        decimal shiftGross = paidShiftOrders.Sum(o => o.Subtotal) + voidShiftOrders.Sum(o => o.Subtotal);
        decimal shiftDiscounts = paidShiftOrders.Sum(o => o.DiscountAmount);
        decimal shiftVoidTotal = voidShiftOrders.Sum(o => o.TotalAmount);
        decimal shiftRefundsAndVoids = Math.Round(shiftVoidTotal + shiftReturnRefunds, 2, MidpointRounding.AwayFromZero);
        decimal shiftNetSales = dto.TotalNetSales;

        var invoices = periodOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.InvoiceNumber))
            .OrderBy(o => o.InvoiceIssuedAt ?? o.CreatedAt)
            .ThenBy(o => o.OrderId)
            .Select(o => o.InvoiceNumber!)
            .ToList();

        bool allBalanced = dto.PaymentReconciliations.All(r => r.Variance == 0m);
        bool hasShort = dto.PaymentReconciliations.Any(r => r.Variance < 0m);
        bool hasOver = dto.PaymentReconciliations.Any(r => r.Variance > 0m);
        string overallStatus = allBalanced
            ? "BALANCED"
            : (hasShort && hasOver ? "MIXED DISCREPANCY" : (hasShort ? "SHORT" : "OVER"));

        decimal cashVar = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

        dto.XReading = new XReadingReportDto
        {
            ShiftId = shift.ShiftId,
            ClientShiftId = shift.ClientShiftId,
            BranchId = shift.BranchId,
            BranchName = branchName,
            BranchCode = branch?.Code,
            BranchAddress = branch?.Address,
            BranchPhone = branch?.Phone,
            OutletId = outletId,
            OutletName = outletName,
            TerminalId = shift.TerminalId,
            TerminalCode = terminalCode,
            TerminalName = terminalName,
            PTUNumber = terminal?.Ptunumber,
            MINNumber = terminal?.Minnumber,
            SerialNumber = terminal?.SerialNumber,
            CashierId = shift.CashierId,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            GeneratedAt = DateTime.UtcNow,
            IsReprint = isReprint,
            ReportingPeriodStart = reportStart,
            ReportingPeriodEnd = periodEnd,
            ReportingPeriodMatchesShift = periodOrders.Count == shiftOrders.Count,
            BeginningInvoiceNumber = invoices.FirstOrDefault(),
            EndingInvoiceNumber = invoices.LastOrDefault(),
            CompletedTransactionsCount = paidPeriodOrders.Count,
            VoidedTransactionsCount = voidPeriodOrders.Count,
            ReturnTransactionsCount = periodReturnCount,
            GrossSales = periodGross,
            TotalDiscounts = periodDiscounts,
            TotalRefundsAndVoids = periodRefundsAndVoids,
            NetSales = periodNetSales,
            VatableSales = paidPeriodOrders.Sum(o => o.VatableSales),
            VatExemptSales = paidPeriodOrders.Sum(o => o.VatExemptSales),
            ZeroRatedSales = paidPeriodOrders.Sum(o => o.ZeroRatedSales),
            VatAmount = paidPeriodOrders.Sum(o => o.VatAmount),
            ShiftTransactionsCount = paidShiftOrders.Count,
            ShiftGrossSales = shiftGross,
            ShiftDiscounts = shiftDiscounts,
            ShiftRefundsAndVoids = shiftRefundsAndVoids,
            ShiftNetSales = shiftNetSales,
            ShiftCashNetSales = Math.Round(shift.TotalCashSales - shift.TotalCashRefunds, 2, MidpointRounding.AwayFromZero),
            ShiftNonCashNetSales = dto.TotalNonCashExpected,
            OpeningFloat = shift.OpeningFloat,
            ExpectedOpeningFund = dto.ExpectedOpeningFund,
            OpeningDiscrepancy = dto.OpeningDiscrepancy,
            OpeningDenominations = dto.OpeningDenominations,
            CashSalesCollected = shift.TotalCashSales,
            CashRefunds = shift.TotalCashRefunds,
            NetCashSales = Math.Round(shift.TotalCashSales - shift.TotalCashRefunds, 2, MidpointRounding.AwayFromZero),
            TotalCashIn = shift.TotalCashIn,
            TotalCashOut = shift.TotalCashOut,
            TotalCashDrops = shift.TotalCashDrops,
            ExpectedCashInDrawer = shift.ExpectedBalance,
            CountedCashInDrawer = countedCash,
            CashVariance = cashVar,
            CashReconciliationStatus = PaymentMethodReconciliationDto.ComputeStatus(cashVar),
            HasRecordedDenominations = dto.HasRecordedDenominations,
            Denominations = dto.Denominations,
            PaymentReconciliations = dto.PaymentReconciliations,
            OriginalDeclaration = dto.OriginalDeclaration,
            RecountHistory = dto.RecountHistory,
            IsOverallBalanced = allBalanced,
            OverallStatus = overallStatus,
            Notes = shift.Notes
        };
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

        List<CashDenominationLineDto> openingDenoms = new();
        if (!string.IsNullOrWhiteSpace(shift.OpeningDenominationsJson))
        {
            try
            {
                openingDenoms = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(shift.OpeningDenominationsJson) ?? new();
            }
            catch { openingDenoms = new(); }
        }

        decimal expectedOpeningFund = shift.ExpectedOpeningFund ?? shift.OpeningFloat;
        decimal openingDiscrepancy = shift.OpeningDiscrepancy
            ?? Math.Round(shift.OpeningFloat - expectedOpeningFund, 2, MidpointRounding.AwayFromZero);

        string shiftBizDate = BusinessDayClock.GetShiftBusinessDateString(shift.OpenedAt, shift.BusinessDate);
        bool isUnresolvedPrevDay = string.Equals(shift.Status, "OPEN", StringComparison.OrdinalIgnoreCase) &&
            BusinessDayClock.IsPreviousBusinessDateShift(shift.OpenedAt, BusinessDayClock.UtcNow(), shift.BusinessDate);

        return new CashDrawerShiftDto
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            OutletId = shift.OutletId ?? 0,
            TerminalId = shift.TerminalId,
            CashierId = shift.CashierId,
            BusinessDate = shiftBizDate,
            IsUnresolvedPreviousBusinessDate = isUnresolvedPrevDay,
            ClosingType = string.IsNullOrWhiteSpace(shift.ClosingType) ? TerminalClosingLockDto.ClosingTypeNormal : shift.ClosingType,
            ClosedBySupervisorId = shift.ClosedBySupervisorId,
            RecoveryReason = shift.RecoveryReason,
            RecoveryStatus = isUnresolvedPrevDay && string.IsNullOrWhiteSpace(shift.RecoveryStatus)
                ? TerminalClosingLockDto.RecoveryStatusRequiresReview
                : shift.RecoveryStatus,
            UnverifiedCashReason = shift.UnverifiedCashReason,
            UnverifiedCashReportedBy = shift.UnverifiedCashReportedBy,
            UnverifiedCashReportedAtUtc = shift.UnverifiedCashReportedAtUtc,
            RecoveryOperationId = shift.RecoveryOperationId,
            OpeningFloat = shift.OpeningFloat,
            ExpectedOpeningFund = expectedOpeningFund,
            OpeningDiscrepancy = openingDiscrepancy,
            OpeningDenominations = openingDenoms,
            OpeningConfirmed = shift.OpeningConfirmed,
            ClientShiftId = shift.ClientShiftId,
            ClientCloseId = shift.ClientCloseId,
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

    public static async Task PopulateMovementHierarchyAsync(
        PosDbContext context,
        IReadOnlyList<CashMovementResponseDto> movements,
        CancellationToken cancellationToken = default)
    {
        if (movements.Count == 0) return;

        var branchIds = movements.Select(m => m.BranchId).Distinct().ToList();
        var terminalIds = movements.Where(m => m.TerminalId > 0).Select(m => m.TerminalId).Distinct().ToList();

        var branchesById = await context.Branches.AsNoTracking()
            .Where(b => branchIds.Contains(b.BranchId))
            .ToDictionaryAsync(b => b.BranchId, cancellationToken);
        var terminalsById = terminalIds.Count > 0
            ? await context.Terminals.AsNoTracking()
                .Include(t => t.Outlet)
                .Where(t => terminalIds.Contains(t.TerminalId))
                .ToDictionaryAsync(t => t.TerminalId, cancellationToken)
            : new Dictionary<int, Terminal>();

        foreach (var m in movements)
        {
            terminalsById.TryGetValue(m.TerminalId, out var term);
            int brId = m.BranchId > 0 ? m.BranchId : (term?.BranchId ?? 0);
            m.BranchName = branchesById.TryGetValue(brId, out var br)
                ? br.Name
                : (brId > 0 ? $"Branch #{brId}" : "Unknown Branch");
            m.OutletId = term?.OutletId ?? 0;
            m.OutletName = term?.Outlet?.Name ?? (m.OutletId > 0 ? $"Outlet #{m.OutletId}" : "Unknown / Legacy Outlet");
            m.TerminalCode = term?.TerminalCode ?? (m.TerminalId > 0 ? $"LEGACY-{m.TerminalId}" : "LEGACY");
            m.TerminalName = term?.Name ?? (m.TerminalId > 0 ? $"Unknown / Legacy Terminal (#{m.TerminalId})" : "Unknown / Legacy Terminal");
        }
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
