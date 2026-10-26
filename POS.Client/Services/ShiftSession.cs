using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;

namespace POS.Client.Services;

public static class ShiftSession
{
    public static bool IsSupervisorRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var clean = role.Trim();
        return clean.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase);
    }

    public static CashDrawerShiftDto RedactForCashier(CashDrawerShiftDto dto)
    {
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

    public static async Task<LocalCashDrawerShift?> CurrentAsync(HttpClient http, int terminalId, bool offline)
    {
        if (!offline)
        {
            var response = await http.GetAsync($"api/cash-drawer/current?terminalId={terminalId}");
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto != null)
                {
                    if (dto.TerminalId != terminalId || dto.Status != "OPEN") throw new InvalidOperationException("Current shift response does not match this drawer.");
                    return await CacheAsync(dto, reconcileCurrent: true);
                }
                throw new InvalidOperationException("Current shift response is empty.");
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException("Unable to verify current shift.");
            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext(); db.InitializeDatabase();
                // A negative ID is a locally confirmed opening still awaiting acknowledgement.
                var pending = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN" && s.ShiftId < 0);
                if (pending != null) return pending;
                foreach (var stale in await db.CashDrawerShifts.Where(s => s.TerminalId == terminalId && s.Status == "OPEN").ToListAsync()) stale.Status = "CLOSED";
                await db.SaveChangesAsync(); return null;
            }
            finally { LocalPosDbContext.WriteLock.Release(); }
        }
        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        return await local.CashDrawerShifts.AsNoTracking().SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN");
    }

    public static async Task<CashDrawerShiftDto?> CurrentDtoAsync(HttpClient http, int terminalId, bool offline, bool isSupervisor = false)
    {
        if (!offline)
        {
            var response = await http.GetAsync($"api/cash-drawer/current?terminalId={terminalId}");
            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto != null)
                {
                    if (dto.TerminalId != terminalId || dto.Status != "OPEN") throw new InvalidOperationException("Current shift response does not match this drawer.");
                    var cached = await CacheAsync(dto, reconcileCurrent: true);
                    using var db = new LocalPosDbContext(); db.InitializeDatabase();
                    return await BuildLocalShiftDtoAsync(db, cached, dto, isReprint: false, includeSupervisorReconciliation: isSupervisor);
                }
                throw new InvalidOperationException("Current shift response is empty.");
            }
            else if (response.StatusCode != HttpStatusCode.NotFound)
                throw new InvalidOperationException("Unable to verify current shift.");

            await LocalPosDbContext.WriteLock.WaitAsync();
            try
            {
                using var db = new LocalPosDbContext(); db.InitializeDatabase();
                var pending = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN" && s.ShiftId < 0);
                if (pending != null) return await BuildLocalShiftDtoAsync(db, pending, includeSupervisorReconciliation: isSupervisor);
                foreach (var stale in await db.CashDrawerShifts.Where(s => s.TerminalId == terminalId && s.Status == "OPEN").ToListAsync()) stale.Status = "CLOSED";
                await db.SaveChangesAsync();
                return null;
            }
            finally { LocalPosDbContext.WriteLock.Release(); }
        }

        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        var localShift = await local.CashDrawerShifts.AsNoTracking().SingleOrDefaultAsync(s => s.TerminalId == terminalId && s.Status == "OPEN");
        return localShift == null ? null : await BuildLocalShiftDtoAsync(local, localShift, includeSupervisorReconciliation: isSupervisor);
    }

    public static async Task<CashDrawerShiftDto?> LastClosedDtoAsync(
        HttpClient http,
        int terminalId,
        bool offline,
        bool isSupervisor = false,
        string? supervisorPin = null)
    {
        if (!offline)
        {
            try
            {
                string url = string.IsNullOrWhiteSpace(supervisorPin)
                    ? $"api/cash-drawer/last-closed/{terminalId}"
                    : $"api/cash-drawer/last-closed/{terminalId}?supervisorPin={Uri.EscapeDataString(supervisorPin.Trim())}";
                var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NoContent)
                {
                    var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                    if (dto != null)
                    {
                        await CacheAsync(dto);
                        return dto;
                    }
                }
            }
            catch
            {
                // Fallback to local SQLite
            }
        }

        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        bool authorizedSupervisor = isSupervisor;
        if (!authorizedSupervisor && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifyLocalSupervisorPinAsync(local, supervisorPin);
            authorizedSupervisor = sup != null;
        }
        var closed = await local.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId && s.Status == "CLOSED")
            .OrderByDescending(s => s.ClosedAt)
            .ThenByDescending(s => s.ShiftId)
            .FirstOrDefaultAsync();
        return closed == null ? null : await BuildLocalShiftDtoAsync(local, closed, isReprint: true, includeSupervisorReconciliation: authorizedSupervisor);
    }

    public static async Task<LocalCashDrawerShift> OpenAsync(HttpClient http, OpenShiftDto request, string cashier, bool offline)
    {
        if (!request.OpeningFloat.HasValue || request.OpeningFloat < 0) throw new InvalidOperationException("Enter opening float. Zero is allowed.");
        request.CashierId = cashier;
        if (string.IsNullOrWhiteSpace(request.ClientShiftId)) throw new InvalidOperationException("Opening identity is required.");
        if (!offline)
        {
            var response = await http.PostAsJsonAsync("api/cash-drawer/open-shift", request);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync());
            return await CacheAsync((await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>())!);
        }
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();
            if (!await db.TerminalConfigs.AnyAsync(t => t.TerminalId == request.TerminalId && t.BranchId == request.BranchId && t.OutletId > 0)) throw new InvalidOperationException("Refresh terminal ownership online before opening offline.");
            var existing = await db.CashDrawerShifts.SingleOrDefaultAsync(s => s.TerminalId == request.TerminalId && s.Status == "OPEN");
            if (existing != null) throw new InvalidOperationException("This drawer already has an open shift.");
            var shift = new LocalCashDrawerShift { ShiftId = -DateTime.UtcNow.Ticks, BranchId = request.BranchId, TerminalId = request.TerminalId, CashierId = cashier, OpeningConfirmed = true, ClientShiftId = request.ClientShiftId, OpeningFloat = Math.Round(request.OpeningFloat.Value, 2), ExpectedBalance = Math.Round(request.OpeningFloat.Value, 2), OpenedAt = request.OpenedAt ?? DateTime.UtcNow };
            db.CashDrawerShifts.Add(shift);
            db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "SHIFT_OPEN", EntityId = shift.ClientShiftId!, PayloadJson = JsonSerializer.Serialize(request), Status = "PENDING", CreatedAt = shift.OpenedAt });
            await db.SaveChangesAsync(); await tx.CommitAsync(); return shift;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    public static async Task<CashDrawerShiftDto> CloseAsync(HttpClient http, CloseShiftDto request, string cashier, bool offline, bool isSupervisor = false)
    {
        if (request == null) throw new InvalidOperationException("Shift closing request is required.");
        if (string.IsNullOrWhiteSpace(cashier)) throw new InvalidOperationException("Authenticated cashier is required to close the shift.");

        bool hasDenominations = request.Denominations != null && request.Denominations.Count > 0;
        bool hasDeclarations = request.PaymentDeclarations != null && request.PaymentDeclarations.Count > 0;
        bool hasStructuredReconciliation = hasDenominations || hasDeclarations;
        if (hasStructuredReconciliation && !request.IsDeclarationConfirmed)
            throw new InvalidOperationException("Cashier must explicitly review and confirm the closing declaration before submitting.");

        decimal countedCash;
        List<CashDenominationLineDto>? validatedDenominations = null;
        if (hasDenominations)
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.Denominations, out var normDenoms, out countedCash, out var denomError))
                throw new InvalidOperationException(denomError ?? "Invalid cash denomination breakdown.");
            validatedDenominations = normDenoms;
        }
        else
        {
            if (request.ActualCountedBalance < 0m)
                throw new InvalidOperationException("Counted cash balance cannot be negative.");
            if (decimal.Round(request.ActualCountedBalance, 2) != request.ActualCountedBalance)
                throw new InvalidOperationException("Counted cash balance supports at most two decimal places.");
            countedCash = Math.Round(request.ActualCountedBalance, 2, MidpointRounding.AwayFromZero);
        }

        if (request.PaymentDeclarations != null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var decl in request.PaymentDeclarations)
            {
                if (decl == null) continue;
                string code = (decl.PaymentMethodCode ?? string.Empty).Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(code))
                    throw new InvalidOperationException("Payment method code is required for all payment declarations.");
                if (code == "CASH") continue;
                if (!seen.Add(code))
                    throw new InvalidOperationException($"Duplicate payment declaration for {code}.");
                if (decl.DeclaredAmount < 0m)
                    throw new InvalidOperationException($"Cashier-declared total for {code} cannot be negative.");
                if (decimal.Round(decl.DeclaredAmount, 2) != decl.DeclaredAmount)
                    throw new InvalidOperationException($"Cashier-declared total for {code} supports at most two decimal places.");
            }
        }

        request.CashierId = cashier;
        request.ActualCountedBalance = countedCash;
        request.Denominations = validatedDenominations;
        request.ClientCloseId = string.IsNullOrWhiteSpace(request.ClientCloseId)
            ? Guid.NewGuid().ToString("N")
            : request.ClientCloseId.Trim();
        request.ClosedAt ??= DateTime.UtcNow;

        if (!offline)
        {
            var response = await http.PostAsJsonAsync("api/cash-drawer/close-shift", request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());

            var serverDto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
            if (serverDto == null)
                throw new InvalidOperationException("Shift closing response was empty.");
            await CacheAsync(serverDto);
            return serverDto;
        }

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();

            LocalCashDrawerShift? shift = null;
            if (request.ShiftId != 0)
                shift = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == request.ShiftId);
            if (shift == null && !string.IsNullOrWhiteSpace(request.ClientShiftId))
                shift = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.ClientShiftId == request.ClientShiftId);

            if (shift == null)
                throw new InvalidOperationException("Active shift not found in local database.");

            if (!string.Equals(shift.CashierId, cashier, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Only the shift owner can close this drawer offline.");

            if (shift.Status == "CLOSED" && !string.IsNullOrWhiteSpace(request.ClientCloseId)
                && string.Equals(shift.ClientCloseId, request.ClientCloseId, StringComparison.Ordinal))
            {
                await tx.RollbackAsync();
                return await BuildLocalShiftDtoAsync(db, shift, isReprint: false, includeSupervisorReconciliation: isSupervisor);
            }

            if (shift.Status != "OPEN")
                throw new InvalidOperationException("Shift is already closed.");

            int pendingMovements = await db.CashDrawerMovements.CountAsync(m => m.ShiftId == shift.ShiftId && m.Status == "PENDING_APPROVAL");
            if (pendingMovements > 0)
                throw new InvalidOperationException($"Cannot close shift while {pendingMovements} cash movement(s) are pending supervisor approval.");

            shift.ExpectedBalance = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
            shift.ActualCountedBalance = countedCash;
            shift.Discrepancy = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

            var reconciliations = await BuildLocalPaymentReconciliationsAsync(db, shift, request.PaymentDeclarations, countedCash);

            shift.Status = "CLOSED";
            shift.ClosedAt = request.ClosedAt.Value >= shift.OpenedAt ? request.ClosedAt.Value : DateTime.UtcNow;
            var trimmedNotes = request.Notes?.Trim();
            shift.Notes = string.IsNullOrWhiteSpace(trimmedNotes)
                ? null
                : (trimmedNotes.Length > 255 ? trimmedNotes[..255] : trimmedNotes);
            shift.ClientCloseId = request.ClientCloseId;
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
                    SubmittedAt = shift.ClosedAt ?? DateTime.UtcNow,
                    TotalCountedCash = countedCash,
                    TotalNonCashDeclared = origDecls.Sum(d => d.DeclaredAmount),
                    Denominations = validatedDenominations ?? CashDenominationCatalog.CreateDefaultLines(),
                    PaymentDeclarations = origDecls,
                    Notes = shift.Notes
                };
                shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(initialDeclaration);
            }

            request.ShiftId = shift.ShiftId;
            request.ClientShiftId = shift.ClientShiftId;
            request.BranchId = shift.BranchId;
            request.TerminalId = shift.TerminalId;
            request.ClosedAt = shift.ClosedAt;

            bool alreadyQueued = await db.SyncOutboxes.AnyAsync(o => o.EntityType == "SHIFT_CLOSE" && o.EntityId == shift.ClientCloseId);
            if (!alreadyQueued)
            {
                db.SyncOutboxes.Add(new LocalSyncOutbox
                {
                    EntityType = "SHIFT_CLOSE",
                    EntityId = shift.ClientCloseId!,
                    PayloadJson = JsonSerializer.Serialize(request),
                    Status = "PENDING",
                    CreatedAt = shift.ClosedAt.Value
                });
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return await BuildLocalShiftDtoAsync(db, shift, isReprint: false, includeSupervisorReconciliation: isSupervisor);
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    public static async Task<CashDrawerShiftDto> RecountAsync(
        HttpClient http,
        long shiftId,
        ShiftRecountRequestDto request,
        string callerUsername,
        string? callerRole,
        bool offline)
    {
        if (request == null) throw new InvalidOperationException("Recount request is required.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new InvalidOperationException("A reason is required for a shift recount or correction.");

        bool hasDenominations = request.Denominations != null && request.Denominations.Count > 0;
        decimal countedCash;
        List<CashDenominationLineDto> validatedDenominations;
        if (hasDenominations)
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.Denominations, out var normDenoms, out countedCash, out var denomError))
                throw new InvalidOperationException(denomError ?? "Invalid cash denomination breakdown.");
            validatedDenominations = normDenoms;
        }
        else
        {
            if (request.ActualCountedBalance < 0m)
                throw new InvalidOperationException("Revised counted cash balance cannot be negative.");
            if (decimal.Round(request.ActualCountedBalance, 2) != request.ActualCountedBalance)
                throw new InvalidOperationException("Revised counted cash balance supports at most two decimal places.");
            countedCash = Math.Round(request.ActualCountedBalance, 2, MidpointRounding.AwayFromZero);
            validatedDenominations = CashDenominationCatalog.CreateDefaultLines();
        }
        request.ActualCountedBalance = countedCash;
        request.Denominations = validatedDenominations;

        if (!offline && shiftId > 0)
        {
            var response = await http.PostAsJsonAsync($"api/cash-drawer/shifts/{shiftId}/recount", request);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await response.Content.ReadAsStringAsync());

            var serverDto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
            if (serverDto == null)
                throw new InvalidOperationException("Recount response was empty.");
            await CacheAsync(serverDto);
            return serverDto;
        }

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();

            string? supervisorUsername = null;
            if (!string.IsNullOrWhiteSpace(request.SupervisorPin))
            {
                var sup = await VerifyLocalSupervisorPinAsync(db, request.SupervisorPin);
                if (sup == null)
                    throw new InvalidOperationException("Invalid supervisor PIN or user does not have supervisor privileges.");
                supervisorUsername = sup.Username;
            }
            else if (IsSupervisorRole(callerRole))
            {
                supervisorUsername = callerUsername;
            }
            else
            {
                throw new InvalidOperationException("Supervisor authorization is required to recount or correct a shift.");
            }

            var shift = await db.CashDrawerShifts.FirstOrDefaultAsync(s => s.ShiftId == shiftId);
            if (shift == null) throw new InvalidOperationException("Shift not found in local database.");
            if (shift.Status != "CLOSED") throw new InvalidOperationException("Only a closed shift can undergo a supervisor recount.");

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
            shift.ExpectedBalance = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
            decimal prevVariance = shift.Discrepancy ?? Math.Round(prevCountedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

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

            shift.ActualCountedBalance = countedCash;
            shift.Discrepancy = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
            var revisedRecons = await BuildLocalPaymentReconciliationsAsync(db, shift, request.PaymentDeclarations, countedCash);
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

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return await BuildLocalShiftDtoAsync(db, shift, isReprint: false, includeSupervisorReconciliation: true);
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    private static async Task<LocalCashier?> VerifyLocalSupervisorPinAsync(LocalPosDbContext db, string? rawPin)
    {
        if (string.IsNullOrWhiteSpace(rawPin)) return null;
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rawPin.Trim())));
        var user = await db.Cashiers.AsNoTracking().FirstOrDefaultAsync(c => c.PinHash == hash);
        if (user == null || !IsSupervisorRole(user.Role)) return null;
        return user;
    }

    public static async Task<LocalCashDrawerShift> CacheAsync(CashDrawerShiftDto dto, bool reconcileCurrent = false)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var transaction = await db.Database.BeginTransactionAsync();
            var shift = await ApplySnapshotAsync(db, dto, reconcileCurrent);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return shift;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    // Caller owns WriteLock and transaction, allowing reference-data refresh to share this exact merge.
    internal static async Task<LocalCashDrawerShift> ApplySnapshotAsync(LocalPosDbContext db, CashDrawerShiftDto dto, bool reconcileCurrent = false)
    {
        var shift = await db.CashDrawerShifts.FindAsync(dto.ShiftId);
        if (shift == null) { shift = new LocalCashDrawerShift { ShiftId = dto.ShiftId }; db.CashDrawerShifts.Add(shift); }
        // Historical opening ACKs never retire a different opening. Only an authoritative
        // current endpoint can retire other acknowledged shifts on this terminal.
        LocalCashDrawerShift? closedLocalPlaceholder = null;
        if (!string.IsNullOrWhiteSpace(dto.ClientShiftId))
        {
            closedLocalPlaceholder = await db.CashDrawerShifts
                .FirstOrDefaultAsync(s => s.TerminalId == dto.TerminalId && s.ShiftId < 0 && s.ClientShiftId == dto.ClientShiftId && s.Status == "CLOSED");
        }
        foreach (var old in await db.CashDrawerShifts.Where(s => s.TerminalId == dto.TerminalId && s.ShiftId != dto.ShiftId && s.Status == "OPEN").ToListAsync())
            if ((old.ShiftId < 0 && !string.IsNullOrWhiteSpace(dto.ClientShiftId) && old.ClientShiftId == dto.ClientShiftId)
                || (reconcileCurrent && old.ShiftId > 0)) old.Status = "CLOSED";
        shift.BranchId = dto.BranchId; shift.TerminalId = dto.TerminalId; shift.CashierId = dto.CashierId;
        shift.OpeningFloat = dto.OpeningFloat; shift.OpeningConfirmed = dto.OpeningConfirmed; shift.ClientShiftId = dto.ClientShiftId;
        if (!string.IsNullOrWhiteSpace(dto.ClientCloseId)) shift.ClientCloseId = dto.ClientCloseId;
        else if (closedLocalPlaceholder != null && !string.IsNullOrWhiteSpace(closedLocalPlaceholder.ClientCloseId)) shift.ClientCloseId = closedLocalPlaceholder.ClientCloseId;
        shift.OpenedAt = dto.OpenedAt;
        shift.ClosedAt = dto.ClosedAt ?? closedLocalPlaceholder?.ClosedAt;
        shift.Status = closedLocalPlaceholder != null ? "CLOSED" : dto.Status;
        if (dto.Notes != null) shift.Notes = dto.Notes;
        else if (closedLocalPlaceholder?.Notes != null) shift.Notes = closedLocalPlaceholder.Notes;
        if (!dto.IsBlindCloseRedacted)
        {
            shift.TotalCashSales = dto.TotalCashSales + await UnacknowledgedCashAsync(db, dto);
            shift.TotalCashRefunds = dto.TotalCashRefunds;
        }
        else
        {
            var unacked = await UnacknowledgedCashAsync(db, dto);
            if (unacked > 0) shift.TotalCashSales = Math.Max(shift.TotalCashSales, unacked);
        }
        shift.TotalCashIn = dto.TotalCashIn; shift.TotalCashOut = dto.TotalCashOut; shift.TotalCashDrops = dto.TotalCashDrops;
        shift.ExpectedBalance = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
        if (dto.ActualCountedBalance.HasValue)
        {
            shift.ActualCountedBalance = dto.ActualCountedBalance;
            shift.Discrepancy = Math.Round(dto.ActualCountedBalance.Value - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
        }
        else if (closedLocalPlaceholder?.ActualCountedBalance != null)
        {
            shift.ActualCountedBalance = closedLocalPlaceholder.ActualCountedBalance;
            shift.Discrepancy = Math.Round(closedLocalPlaceholder.ActualCountedBalance.Value - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
        }
        if (dto.Denominations != null && dto.Denominations.Count > 0)
            shift.ClosingDenominationsJson = JsonSerializer.Serialize(dto.Denominations);
        else if (!string.IsNullOrWhiteSpace(closedLocalPlaceholder?.ClosingDenominationsJson))
            shift.ClosingDenominationsJson = closedLocalPlaceholder.ClosingDenominationsJson;
        if (dto.OriginalDeclaration != null)
            shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(dto.OriginalDeclaration);
        else if (dto.CashierDeclaration != null && string.IsNullOrWhiteSpace(shift.ClosingOriginalDeclarationJson))
            shift.ClosingOriginalDeclarationJson = JsonSerializer.Serialize(dto.CashierDeclaration);
        else if (!string.IsNullOrWhiteSpace(closedLocalPlaceholder?.ClosingOriginalDeclarationJson))
            shift.ClosingOriginalDeclarationJson = closedLocalPlaceholder.ClosingOriginalDeclarationJson;
        if (dto.PaymentReconciliations != null && dto.PaymentReconciliations.Count > 0)
            shift.ClosingPaymentReconciliationsJson = JsonSerializer.Serialize(dto.PaymentReconciliations);
        else if (!string.IsNullOrWhiteSpace(closedLocalPlaceholder?.ClosingPaymentReconciliationsJson))
            shift.ClosingPaymentReconciliationsJson = closedLocalPlaceholder.ClosingPaymentReconciliationsJson;
        if (dto.RecountHistory != null && dto.RecountHistory.Count > 0)
            shift.ClosingRecountHistoryJson = JsonSerializer.Serialize(dto.RecountHistory);
        else if (!string.IsNullOrWhiteSpace(closedLocalPlaceholder?.ClosingRecountHistoryJson))
            shift.ClosingRecountHistoryJson = closedLocalPlaceholder.ClosingRecountHistoryJson;
        return shift;
    }

    public static async Task<List<PaymentMethodReconciliationDto>> BuildLocalPaymentReconciliationsAsync(
        LocalPosDbContext db,
        LocalCashDrawerShift shift,
        IReadOnlyList<PaymentMethodDeclarationDto>? declarations = null,
        decimal? overrideCountedCash = null)
    {
        var configuredMethods = await db.PaymentMethods.AsNoTracking()
            .OrderBy(pm => pm.DisplayOrder)
            .ThenBy(pm => pm.Name)
            .ToListAsync();

        var shiftOrders = await GetLocalOrdersForShiftAsync(db, shift);
        var shiftOrderIds = shiftOrders.Select(o => o.OrderId).ToHashSet(StringComparer.Ordinal);
        var shiftPayments = shiftOrderIds.Count > 0
            ? await db.Payments.AsNoTracking().Where(p => shiftOrderIds.Contains(p.OrderId)).ToListAsync()
            : new List<LocalPayment>();

        var list = new List<PaymentMethodReconciliationDto>();

        decimal expectedCash = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
        decimal countedCash = overrideCountedCash ?? shift.ActualCountedBalance ?? 0m;
        decimal cashVariance = Math.Round(countedCash - expectedCash, 2, MidpointRounding.AwayFromZero);

        list.Add(new PaymentMethodReconciliationDto
        {
            PaymentMethodCode = "CASH",
            PaymentMethodName = "Cash (Drawer)",
            IsCash = true,
            IsActive = true,
            DisplayOrder = 0,
            GrossSalesAmount = shift.TotalCashSales,
            RefundAmount = shift.TotalCashRefunds,
            NetSalesAmount = Math.Round(shift.TotalCashSales - shift.TotalCashRefunds, 2, MidpointRounding.AwayFromZero),
            ExpectedAmount = expectedCash,
            DeclaredAmount = countedCash,
            Variance = cashVariance,
            Status = PaymentMethodReconciliationDto.ComputeStatus(cashVariance)
        });

        var nonCashCodes = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CASH" };

        foreach (var pm in configuredMethods.Where(m => m.IsActive))
        {
            if (seen.Add(pm.Code)) nonCashCodes.Add(pm.Code);
        }
        foreach (var p in shiftPayments)
        {
            if (!string.IsNullOrWhiteSpace(p.PaymentMethod) && seen.Add(p.PaymentMethod.Trim()))
                nonCashCodes.Add(p.PaymentMethod.Trim().ToUpperInvariant());
        }
        if (declarations != null)
        {
            foreach (var d in declarations)
            {
                if (d != null && !string.IsNullOrWhiteSpace(d.PaymentMethodCode) && seen.Add(d.PaymentMethodCode.Trim()))
                    nonCashCodes.Add(d.PaymentMethodCode.Trim().ToUpperInvariant());
            }
        }

        foreach (var code in nonCashCodes)
        {
            var cfg = configuredMethods.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
            decimal gross = shiftPayments
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                            && (p.PaymentStatus == "SUCCESS" || p.PaymentStatus == "REFUNDED"))
                .Sum(p => p.Amount);
            decimal refund = shiftPayments
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                            && p.PaymentStatus == "REFUNDED")
                .Sum(p => p.Amount);
            decimal net = Math.Round(gross - refund, 2, MidpointRounding.AwayFromZero);
            decimal expected = net;
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
                RefundAmount = refund,
                NetSalesAmount = net,
                ExpectedAmount = expected,
                DeclaredAmount = declared,
                Variance = variance,
                Status = PaymentMethodReconciliationDto.ComputeStatus(variance)
            });
        }

        return list.OrderBy(r => r.IsCash ? 0 : 1).ThenBy(r => r.DisplayOrder).ThenBy(r => r.PaymentMethodName).ToList();
    }

    public static async Task<CashDrawerShiftDto> BuildLocalShiftDtoAsync(
        LocalPosDbContext db,
        LocalCashDrawerShift shift,
        CashDrawerShiftDto? serverSnapshot = null,
        bool isReprint = false,
        bool includeSupervisorReconciliation = false)
    {
        var localMovements = await db.CashDrawerMovements.AsNoTracking()
            .Where(m => m.ShiftId == shift.ShiftId)
            .OrderByDescending(m => m.CreatedAt)
            .ToListAsync();

        var movements = serverSnapshot?.Movements != null && serverSnapshot.Movements.Count > 0
            ? serverSnapshot.Movements
            : localMovements.Select(m => new CashMovementResponseDto
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
                ReferenceNumber = m.ReferenceNumber,
                ClientMovementId = m.ClientMovementId,
                Status = m.Status,
                CreatedBy = m.CreatedBy,
                AuthorizedBy = m.AuthorizedBy,
                ApprovedBy = m.ApprovedBy,
                CreatedAt = m.CreatedAt,
                ApprovedAt = m.ApprovedAt,
                PostedAt = m.PostedAt,
                SyncStatus = m.SyncStatus
            }).ToList();

        var dto = new CashDrawerShiftDto
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            TerminalId = shift.TerminalId,
            CashierId = shift.CashierId,
            OpeningFloat = shift.OpeningFloat,
            OpeningConfirmed = shift.OpeningConfirmed,
            ClientShiftId = shift.ClientShiftId,
            ClientCloseId = shift.ClientCloseId,
            TotalCashSales = shift.TotalCashSales,
            TotalCashRefunds = shift.TotalCashRefunds,
            TotalCashIn = shift.TotalCashIn,
            TotalCashOut = shift.TotalCashOut,
            TotalCashDrops = shift.TotalCashDrops,
            TotalPettyCashOut = movements.Where(m => m.Status == "POSTED" && m.MovementType == "PETTY_CASH").Sum(m => m.Amount),
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

        if (!string.IsNullOrWhiteSpace(shift.ClosingDenominationsJson))
        {
            try { dto.Denominations = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(shift.ClosingDenominationsJson) ?? new(); }
            catch { dto.Denominations = new(); }
        }

        List<PaymentMethodReconciliationDto>? reconciliations = null;
        if (shift.Status == "CLOSED" && !string.IsNullOrWhiteSpace(shift.ClosingPaymentReconciliationsJson))
        {
            try { reconciliations = JsonSerializer.Deserialize<List<PaymentMethodReconciliationDto>>(shift.ClosingPaymentReconciliationsJson); }
            catch { reconciliations = null; }
        }

        if (reconciliations == null || reconciliations.Count == 0)
        {
            if (serverSnapshot?.PaymentReconciliations != null && serverSnapshot.PaymentReconciliations.Count > 0)
            {
                reconciliations = serverSnapshot.PaymentReconciliations;
                var cashRow = reconciliations.FirstOrDefault(r => r.IsCash);
                if (cashRow != null)
                {
                    cashRow.GrossSalesAmount = shift.TotalCashSales;
                    cashRow.NetSalesAmount = Math.Round(shift.TotalCashSales - shift.TotalCashRefunds, 2, MidpointRounding.AwayFromZero);
                    cashRow.ExpectedAmount = shift.ExpectedBalance;
                    cashRow.DeclaredAmount = shift.ActualCountedBalance ?? 0m;
                    cashRow.Variance = Math.Round(cashRow.DeclaredAmount - cashRow.ExpectedAmount, 2, MidpointRounding.AwayFromZero);
                    cashRow.Status = PaymentMethodReconciliationDto.ComputeStatus(cashRow.Variance);
                }
            }
            else
            {
                reconciliations = await BuildLocalPaymentReconciliationsAsync(db, shift);
            }
        }

        dto.PaymentReconciliations = reconciliations;
        dto.TotalNonCashExpected = dto.PaymentReconciliations.Where(r => !r.IsCash).Sum(r => r.ExpectedAmount);
        dto.TotalNonCashDeclared = dto.PaymentReconciliations.Where(r => !r.IsCash).Sum(r => r.DeclaredAmount);
        dto.TotalNetSales = Math.Round((shift.TotalCashSales - shift.TotalCashRefunds) + dto.TotalNonCashExpected, 2, MidpointRounding.AwayFromZero);
        dto.ConfiguredNonCashMethods = serverSnapshot?.ConfiguredNonCashMethods != null && serverSnapshot.ConfiguredNonCashMethods.Count > 0
            ? serverSnapshot.ConfiguredNonCashMethods
            : dto.PaymentReconciliations
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
            try { dto.OriginalDeclaration = JsonSerializer.Deserialize<CashierDeclarationReportDto>(shift.ClosingOriginalDeclarationJson); }
            catch { dto.OriginalDeclaration = null; }
        }
        if (!string.IsNullOrWhiteSpace(shift.ClosingRecountHistoryJson))
        {
            try { dto.RecountHistory = JsonSerializer.Deserialize<List<ShiftRecountAuditDto>>(shift.ClosingRecountHistoryJson) ?? new(); }
            catch { dto.RecountHistory = new(); }
        }

        var terminalCfg = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == shift.TerminalId);
        decimal countedCash = shift.ActualCountedBalance ?? dto.Denominations.Sum(d => d.Subtotal);
        dto.CashierDeclaration = serverSnapshot?.CashierDeclaration ?? new CashierDeclarationReportDto
        {
            ShiftId = shift.ShiftId,
            ClientShiftId = shift.ClientShiftId,
            BranchId = shift.BranchId,
            BranchName = terminalCfg?.BranchName ?? "Main Branch",
            BranchCode = terminalCfg?.BranchCode,
            BranchAddress = terminalCfg?.BranchAddress,
            BranchPhone = terminalCfg?.BranchPhone,
            OutletId = terminalCfg?.OutletId ?? 0,
            OutletName = terminalCfg?.OutletName ?? string.Empty,
            TerminalId = shift.TerminalId,
            TerminalCode = terminalCfg?.TerminalCode ?? string.Empty,
            TerminalName = terminalCfg?.TerminalName ?? $"Terminal #{shift.TerminalId}",
            PTUNumber = terminalCfg?.PTUNumber,
            MINNumber = terminalCfg?.MINNumber,
            SerialNumber = terminalCfg?.SerialNumber,
            CashierId = shift.CashierId,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            SubmittedAt = shift.ClosedAt ?? DateTime.UtcNow,
            IsReprint = isReprint,
            TotalCountedCash = countedCash,
            TotalNonCashDeclared = dto.TotalNonCashDeclared,
            Denominations = dto.Denominations.Count > 0 ? dto.Denominations : CashDenominationCatalog.CreateDefaultLines(),
            PaymentDeclarations = dto.ConfiguredNonCashMethods,
            Notes = shift.Notes
        };
        if (isReprint && dto.CashierDeclaration != null)
            dto.CashierDeclaration.IsReprint = true;

        var shiftOrders = await GetLocalOrdersForShiftAsync(db, shift);
        var invoices = shiftOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.InvoiceNumber))
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.InvoiceNumber)
            .ToList();

        bool allBalanced = dto.PaymentReconciliations.All(r => r.Variance == 0m);
        bool hasShort = dto.PaymentReconciliations.Any(r => r.Variance < 0m);
        bool hasOver = dto.PaymentReconciliations.Any(r => r.Variance > 0m);
        string overallStatus = allBalanced
            ? "BALANCED"
            : (hasShort && hasOver ? "MIXED DISCREPANCY" : (hasShort ? "SHORT" : "OVER"));

        decimal cashVar = Math.Round(countedCash - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);

        dto.XReading = serverSnapshot?.XReading ?? new XReadingReportDto
        {
            ShiftId = shift.ShiftId,
            ClientShiftId = shift.ClientShiftId,
            BranchId = shift.BranchId,
            BranchName = terminalCfg?.BranchName ?? "Main Branch",
            BranchCode = terminalCfg?.BranchCode,
            BranchAddress = terminalCfg?.BranchAddress,
            BranchPhone = terminalCfg?.BranchPhone,
            OutletId = terminalCfg?.OutletId ?? 0,
            OutletName = terminalCfg?.OutletName ?? string.Empty,
            TerminalId = shift.TerminalId,
            TerminalCode = terminalCfg?.TerminalCode ?? string.Empty,
            TerminalName = terminalCfg?.TerminalName ?? $"Terminal #{shift.TerminalId}",
            PTUNumber = terminalCfg?.PTUNumber,
            MINNumber = terminalCfg?.MINNumber,
            SerialNumber = terminalCfg?.SerialNumber,
            CashierId = shift.CashierId,
            OpenedAt = shift.OpenedAt,
            ClosedAt = shift.ClosedAt,
            GeneratedAt = DateTime.UtcNow,
            IsReprint = isReprint,
            ReportingPeriodStart = shift.OpenedAt,
            ReportingPeriodEnd = shift.ClosedAt ?? DateTime.UtcNow,
            ReportingPeriodMatchesShift = true,
            BeginningInvoiceNumber = invoices.FirstOrDefault(),
            EndingInvoiceNumber = invoices.LastOrDefault(),
            CompletedTransactionsCount = shiftOrders.Count,
            GrossSales = shiftOrders.Sum(o => o.Subtotal),
            TotalDiscounts = shiftOrders.Sum(o => o.DiscountAmount),
            TotalRefundsAndVoids = shift.TotalCashRefunds,
            NetSales = dto.TotalNetSales,
            VatableSales = shiftOrders.Sum(o => o.VatableSales),
            VatExemptSales = shiftOrders.Sum(o => o.VatExemptSales),
            ZeroRatedSales = shiftOrders.Sum(o => o.ZeroRatedSales),
            VatAmount = shiftOrders.Sum(o => o.VatAmount),
            ShiftTransactionsCount = shiftOrders.Count,
            ShiftGrossSales = shiftOrders.Sum(o => o.Subtotal),
            ShiftDiscounts = shiftOrders.Sum(o => o.DiscountAmount),
            ShiftRefundsAndVoids = shift.TotalCashRefunds,
            ShiftNetSales = dto.TotalNetSales,
            ShiftCashNetSales = Math.Round(shift.TotalCashSales - shift.TotalCashRefunds, 2, MidpointRounding.AwayFromZero),
            ShiftNonCashNetSales = dto.TotalNonCashExpected,
            OpeningFloat = shift.OpeningFloat,
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
            Denominations = dto.Denominations,
            PaymentReconciliations = dto.PaymentReconciliations,
            OriginalDeclaration = dto.OriginalDeclaration,
            RecountHistory = dto.RecountHistory,
            IsOverallBalanced = allBalanced,
            OverallStatus = overallStatus,
            Notes = shift.Notes
        };
        if (isReprint && dto.XReading != null)
            dto.XReading.IsReprint = true;

        return includeSupervisorReconciliation ? dto : RedactForCashier(dto);
    }

    private static async Task<List<LocalOrder>> GetLocalOrdersForShiftAsync(LocalPosDbContext db, LocalCashDrawerShift shift)
    {
        var outboxes = await db.SyncOutboxes.AsNoTracking().Where(o => o.EntityType == "Order").ToListAsync();
        var orderIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outbox in outboxes)
        {
            var payload = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>(outbox.PayloadJson);
            if (payload == null) continue;
            bool match = !string.IsNullOrWhiteSpace(payload.ClientShiftId) && !string.IsNullOrWhiteSpace(shift.ClientShiftId)
                ? payload.ClientShiftId == shift.ClientShiftId
                : payload.ShiftId == shift.ShiftId;
            if (match && payload.TerminalId == shift.TerminalId && payload.BranchId == shift.BranchId)
                orderIds.Add(outbox.EntityId);
        }

        var allOrders = await db.Orders.AsNoTracking()
            .Where(o => o.TerminalId == shift.TerminalId && o.BranchId == shift.BranchId && o.PaymentStatus == "SUCCESS")
            .ToListAsync();

        return allOrders
            .Where(o => orderIds.Contains(o.OrderId)
                        || (o.ShiftId.HasValue && o.ShiftId.Value == shift.ShiftId)
                        || (!string.IsNullOrWhiteSpace(o.ClientShiftId) && o.ClientShiftId == shift.ClientShiftId))
            .OrderBy(o => o.CreatedAt)
            .ToList();
    }

    private static async Task<decimal> UnacknowledgedCashAsync(LocalPosDbContext db, CashDrawerShiftDto dto)
    {
        var outboxes = await db.SyncOutboxes.AsNoTracking().Where(o => o.EntityType == "Order").ToListAsync();
        var accepted = dto.AcknowledgedLocalTransactionIds?.ToHashSet(StringComparer.Ordinal);
        var pendingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var outbox in outboxes)
        {
            // Snapshot IDs, not present-day outbox status, decide membership: an upload ACK
            // can arrive after the server read the totals but before this local write lock.
            if (accepted != null ? accepted.Contains(outbox.EntityId) : outbox.Status == "SYNCED") continue;
            var order = POS.Shared.Services.LegacyTerminalPayload.Deserialize<OrderCreateDto>(outbox.PayloadJson);
            if (order == null) throw new InvalidOperationException("Cannot reconcile an unreadable offline order.");
            var matches = !string.IsNullOrWhiteSpace(order.ClientShiftId)
                ? order.ClientShiftId == dto.ClientShiftId
                : order.ShiftId == dto.ShiftId;
            if (matches && order.TerminalId == dto.TerminalId && order.BranchId == dto.BranchId) pendingIds.Add(outbox.EntityId);
        }
        if (pendingIds.Count == 0) return 0;
        if (accepted == null) throw new InvalidOperationException("Refresh against an updated server before reconciling pending cash sales; exact transaction acknowledgement IDs are required.");
        var payments = await (from payment in db.Payments.AsNoTracking()
                              join order in db.Orders.AsNoTracking() on payment.OrderId equals order.OrderId
                              where order.TerminalId == dto.TerminalId && order.BranchId == dto.BranchId && order.CashierId == dto.CashierId
                                  && payment.PaymentMethod == "CASH" && payment.PaymentStatus == "SUCCESS"
                              select payment).ToListAsync();
        return payments.Where(p => pendingIds.Contains(p.OrderId)).Sum(p => p.Amount);
    }
}
