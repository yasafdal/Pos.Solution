using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Models;
using POS.Shared.Services;

namespace POS.Client.Services;

public enum ShiftVerificationIssue
{
    None = 0,
    InvalidSession = 1,
    LocalDatabaseError = 2,
    Locked = 3,
    BranchMismatch = 4,
    DifferentCashierOpen = 5,
    ClosedShift = 6,
    NoActiveShift = 7,
    OpeningNotConfirmed = 8,
    MissingOpeningConfig = 9,
    AuthorizationRejected = 10
}

public class ShiftVerificationResult
{
    public bool IsReady { get; set; }
    public bool Offline { get; set; }
    public ShiftVerificationIssue Issue { get; set; } = ShiftVerificationIssue.None;
    public LocalCashDrawerShift? Shift { get; set; }
    public TerminalClosingLockDto? LockStatus { get; set; }
    public TerminalCashFundConfigDto? OpeningFundConfig { get; set; }
    public string? ErrorMessage { get; set; }
}

public static class ShiftSession
{
    private readonly record struct CurrentShiftFetchResult(
        bool UseLocalFallback,
        bool IsNotFound,
        CashDrawerShiftDto? Dto,
        HttpStatusCode? AuthoritativeRejectionStatus = null,
        string? ErrorMessage = null);

    private readonly record struct OnlineOperationOutcome<T>(
        bool SucceededOnline,
        bool UseLocalFallback,
        T? Result,
        HttpStatusCode? RejectionStatus = null,
        string? RejectionMessage = null);

    public static async Task<LocalCashDrawerShift?> GetLocalActiveOpenShiftAsync(
        LocalPosDbContext db,
        int terminalId,
        bool asNoTracking = false)
    {
        var query = asNoTracking
            ? db.CashDrawerShifts.AsNoTracking()
            : db.CashDrawerShifts.AsQueryable();

        var openShifts = (await query
            .Where(s => s.TerminalId == terminalId && s.Status == "OPEN")
            .ToListAsync())
            .OrderByDescending(s => s.ShiftId > 0)
            .ThenByDescending(s => s.OpenedAt)
            .ThenByDescending(s => s.ShiftId)
            .ToList();

        if (openShifts.Count == 0)
            return null;

        if (openShifts.Count == 1)
            return openShifts[0];

        var firstClientShiftId = openShifts[0].ClientShiftId?.Trim();
        if (!string.IsNullOrWhiteSpace(firstClientShiftId) &&
            openShifts.All(s => string.Equals(s.ClientShiftId?.Trim(), firstClientShiftId, StringComparison.Ordinal)))
        {
            return openShifts[0];
        }

        throw new InvalidOperationException("Multiple conflicting open shifts detected in local database for this terminal. Reconcile or close duplicate shifts before continuing.");
    }

    private static string BuildHttpErrorMessage(string prefix, HttpStatusCode statusCode, string? body)
    {
        var detail = string.IsNullOrWhiteSpace(body) ? statusCode.ToString() : body.Trim();
        return $"{prefix} (HTTP {(int)statusCode} {statusCode}): {detail}";
    }

    private static async Task<string> SafeReadErrorMessageAsync(HttpResponseMessage response)
    {
        try
        {
            return (await response.Content.ReadAsStringAsync()).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AttachActiveTerminalTokenIfMissing(HttpClient http)
    {
        if (http.DefaultRequestHeaders.Authorization == null &&
            !string.IsNullOrWhiteSpace(DeviceActivationService.ActiveTerminalToken) &&
            !string.Equals(DeviceActivationService.ActiveTerminalToken, "OFFLINE-SESSION-TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", DeviceActivationService.ActiveTerminalToken);
        }
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode) =>
        (int)statusCode >= 500 || statusCode == HttpStatusCode.RequestTimeout;

    private static async Task<CurrentShiftFetchResult> FetchCurrentShiftOnlineAsync(HttpClient http, int terminalId)
    {
        if (string.Equals(http.DefaultRequestHeaders.Authorization?.Parameter, "OFFLINE-SESSION-TOKEN", StringComparison.OrdinalIgnoreCase))
            return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);

        AttachActiveTerminalTokenIfMissing(http);

        HttpResponseMessage? response = null;
        try
        {
            response = await http.GetAsync($"api/cash-drawer/current?terminalId={terminalId}");
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await DeviceActivationService.EnsureOnlineBearerTokenWithStatusAsync(http, null, forceRefresh: true);
                if (refreshed.RejectionStatus is HttpStatusCode.Forbidden or HttpStatusCode.Locked)
                {
                    return new CurrentShiftFetchResult(
                        UseLocalFallback: false,
                        IsNotFound: false,
                        Dto: null,
                        AuthoritativeRejectionStatus: refreshed.RejectionStatus,
                        ErrorMessage: BuildHttpErrorMessage("Shift verification rejected", refreshed.RejectionStatus.Value, refreshed.ErrorMessage));
                }

                if (refreshed.IsTransientError)
                    return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);

                if (refreshed.Success)
                {
                    response.Dispose();
                    response = await http.GetAsync($"api/cash-drawer/current?terminalId={terminalId}");
                }
            }

            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto == null)
                    throw new InvalidOperationException("Current shift response is empty.");
                if (dto.TerminalId != terminalId || dto.Status != "OPEN")
                    throw new InvalidOperationException("Current shift response does not match this drawer.");
                return new CurrentShiftFetchResult(UseLocalFallback: false, IsNotFound: false, Dto: dto);
            }

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new CurrentShiftFetchResult(UseLocalFallback: false, IsNotFound: true, Dto: null);

            if (IsTransientStatusCode(response.StatusCode))
                return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);

            string errBody = await SafeReadErrorMessageAsync(response);
            return new CurrentShiftFetchResult(
                UseLocalFallback: false,
                IsNotFound: false,
                Dto: null,
                AuthoritativeRejectionStatus: response.StatusCode,
                ErrorMessage: BuildHttpErrorMessage("Shift verification failed", response.StatusCode, errBody));
        }
        catch (HttpRequestException)
        {
            return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);
        }
        catch (TaskCanceledException)
        {
            return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);
        }
        catch (TimeoutException)
        {
            return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);
        }
        catch (JsonException)
        {
            return new CurrentShiftFetchResult(UseLocalFallback: true, IsNotFound: false, Dto: null);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<LocalCashDrawerShift?> RetireStaleLocalOpenShiftsAsync(int terminalId)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            // A negative ID is a locally confirmed opening still awaiting acknowledgement.
            var pending = await db.CashDrawerShifts
                .Where(s => s.TerminalId == terminalId && s.Status == "OPEN" && s.ShiftId < 0)
                .OrderByDescending(s => s.OpenedAt)
                .ThenByDescending(s => s.ShiftId)
                .FirstOrDefaultAsync();
            if (pending != null) return pending;
            foreach (var stale in await db.CashDrawerShifts.Where(s => s.TerminalId == terminalId && s.Status == "OPEN").ToListAsync())
                stale.Status = "CLOSED";
            await db.SaveChangesAsync();
            return null;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    private static bool IsClosedInCurrentBusinessDay(LocalCashDrawerShift? closedShift, DateTime utcNow)
    {
        if (closedShift == null || !string.Equals(closedShift.Status, "CLOSED", StringComparison.OrdinalIgnoreCase))
            return false;

        DateTime closedRefUtc = BusinessDayClock.NormalizeUtc(closedShift.ClosedAt ?? closedShift.OpenedAt);
        DateTime nextDayStartUtc = BusinessDayClock.GetNextBusinessDayStartUtc(closedRefUtc);
        return utcNow < nextDayStartUtc;
    }

    private static OpenShiftDto BuildOpenShiftDtoFromLocal(LocalCashDrawerShift localShift)
    {
        List<CashDenominationLineDto>? openDenoms = null;
        if (!string.IsNullOrWhiteSpace(localShift.OpeningDenominationsJson))
        {
            try { openDenoms = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(localShift.OpeningDenominationsJson); }
            catch { openDenoms = null; }
        }

        return new OpenShiftDto
        {
            TerminalId = localShift.TerminalId,
            BranchId = localShift.BranchId,
            OpeningFloat = localShift.OpeningFloat,
            ExpectedOpeningFund = localShift.ExpectedOpeningFund,
            OpeningDiscrepancy = localShift.OpeningDiscrepancy,
            OpeningDenominations = openDenoms,
            ClientShiftId = localShift.ClientShiftId,
            CashierId = localShift.CashierId,
            OpenedAt = localShift.OpenedAt,
            Notes = localShift.Notes
        };
    }

    public static async Task<ShiftVerificationResult> VerifyPaymentShiftLocalFirstAsync(
        HttpClient http,
        PosSessionService session,
        IPosConnectivityService connectivity)
    {
        if (session == null || !session.IsAuthenticated || string.IsNullOrWhiteSpace(session.Username) ||
            session.Terminal == null || session.Terminal.TerminalId <= 0 || session.Terminal.BranchId <= 0 || session.Terminal.OutletId <= 0)
        {
            return new ShiftVerificationResult
            {
                IsReady = false,
                Issue = ShiftVerificationIssue.InvalidSession,
                ErrorMessage = "Cashier session or assigned terminal, branch, and outlet are invalid. Please sign in again."
            };
        }

        int terminalId = session.Terminal.TerminalId;
        int branchId = session.Terminal.BranchId;
        int outletId = session.Terminal.OutletId;
        string username = session.Username.Trim();

        LocalTerminalConfig? localTerm;
        TerminalClosingLockDto localLock;
        LocalCashDrawerShift? localOpenShift;
        LocalCashDrawerShift? localLatestClosedShift = null;
        bool localClosedForCurrentDay = false;

        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            localTerm = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                        ?? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();

            if (localTerm != null &&
                (localTerm.TerminalId != terminalId || localTerm.BranchId != branchId || (localTerm.OutletId > 0 && localTerm.OutletId != outletId)))
            {
                return new ShiftVerificationResult
                {
                    IsReady = false,
                    Offline = true,
                    Issue = ShiftVerificationIssue.BranchMismatch,
                    ErrorMessage = "Terminal branch or outlet ownership does not match local device configuration. Refresh terminal data online."
                };
            }

            DateTime nowUtc = BusinessDayClock.UtcNow();
            long nowTicks = BusinessDayClock.TickCount64();
            var localActiveRestrictions = await GetLocalActiveCashierRestrictionsAsync(db, terminalId, nowUtc, nowTicks);
            localLock = localTerm != null
                ? BusinessDayClock.EvaluateLock(
                    terminalId: localTerm.TerminalId,
                    closedBusinessDate: localTerm.ClosedBusinessDate,
                    lockedByCashier: localTerm.ClosingLockedByCashier,
                    lockedAtUtc: localTerm.ClosingLockedAtUtc,
                    unlockAtUtc: localTerm.ClosingUnlockAtUtc,
                    candidateCashierUsername: username,
                    authoritativeServerUtc: null,
                    deviceUtcNow: nowUtc,
                    currentTickCount64: nowTicks,
                    lockedAtTickCount64: localTerm.ClosingLockedTickCount64,
                    lastKnownServerUtc: localTerm.LastKnownServerUtc,
                    lastKnownServerTickCount64: localTerm.LastKnownServerTickCount64,
                    additionalClosedCashiersToday: localActiveRestrictions.Select(r => r.CashierId))
                : BusinessDayClock.EvaluateLock(
                    terminalId: terminalId,
                    closedBusinessDate: null,
                    lockedByCashier: null,
                    lockedAtUtc: null,
                    unlockAtUtc: null,
                    candidateCashierUsername: username,
                    authoritativeServerUtc: null,
                    deviceUtcNow: nowUtc,
                    currentTickCount64: nowTicks,
                    additionalClosedCashiersToday: localActiveRestrictions.Select(r => r.CashierId));
            localLock.ActiveCashierRestrictions = localActiveRestrictions;

            if (localLock.IsCashierRestricted || localLock.IsTerminalLocked)
            {
                return new ShiftVerificationResult
                {
                    IsReady = false,
                    Offline = true,
                    Issue = ShiftVerificationIssue.Locked,
                    LockStatus = localLock,
                    ErrorMessage = localLock.CashierRestrictionMessage ?? localLock.LockMessage ?? "POS Terminal is locked for the current business day."
                };
            }

            localOpenShift = await GetLocalActiveOpenShiftAsync(db, terminalId, asNoTracking: true);
            if (localOpenShift != null)
            {
                if (localOpenShift.BranchId != branchId)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = true,
                        Issue = ShiftVerificationIssue.BranchMismatch,
                        Shift = localOpenShift,
                        LockStatus = localLock,
                        ErrorMessage = "Open shift branch does not match the active terminal branch."
                    };
                }

                if (!string.Equals(localOpenShift.CashierId, username, StringComparison.OrdinalIgnoreCase))
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = true,
                        Issue = ShiftVerificationIssue.DifferentCashierOpen,
                        Shift = localOpenShift,
                        LockStatus = localLock,
                        ErrorMessage = TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage
                    };
                }
            }
            else
            {
                string currentBizDate = BusinessDayClock.GetBusinessDateString(nowUtc);
                string normalizedUser = username.ToLowerInvariant();
                localLatestClosedShift = (await db.CashDrawerShifts.AsNoTracking()
                    .Where(s => s.TerminalId == terminalId && s.Status == "CLOSED" && s.CashierId.ToLower() == normalizedUser)
                    .ToListAsync())
                    .OrderByDescending(s => s.ClosedAt ?? s.OpenedAt)
                    .ThenByDescending(s => s.ShiftId)
                    .FirstOrDefault();

                bool wasReopenedToday = await db.CashierClosingRestrictions.AsNoTracking()
                    .AnyAsync(r => r.TerminalId == terminalId
                                && r.BusinessDate == currentBizDate
                                && r.Status == "REOPENED"
                                && r.CashierId.ToLower() == normalizedUser);

                localClosedForCurrentDay = !wasReopenedToday && IsClosedInCurrentBusinessDay(localLatestClosedShift, nowUtc);
            }
        }
        catch (Exception ex)
        {
            return new ShiftVerificationResult
            {
                IsReady = false,
                Offline = true,
                Issue = ShiftVerificationIssue.LocalDatabaseError,
                ErrorMessage = $"Unable to read local shift database: {ex.Message}"
            };
        }

        TerminalConnectivityState connectivityState;
        try
        {
            connectivityState = await connectivity.CheckConnectivityAsync();
        }
        catch
        {
            connectivityState = TerminalConnectivityState.InternetConnectedApiUnavailable;
        }

        bool offline = connectivityState != TerminalConnectivityState.OnlineAndHealthy;
        if (!offline)
        {
            var tokenResult = await DeviceActivationService.EnsureOnlineBearerTokenWithStatusAsync(http, session, forceRefresh: false);
            if (!tokenResult.Success)
            {
                if (tokenResult.RejectionStatus == HttpStatusCode.Locked)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = false,
                        Issue = ShiftVerificationIssue.Locked,
                        ErrorMessage = tokenResult.ErrorMessage ?? "POS Terminal is locked for the current business day."
                    };
                }

                if (tokenResult.RejectionStatus is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = false,
                        Issue = ShiftVerificationIssue.AuthorizationRejected,
                        ErrorMessage = tokenResult.ErrorMessage ?? "Online session authorization was rejected by the server."
                    };
                }

                if (tokenResult.IsTransientError || !DeviceActivationService.HasValidOnlineBearerToken(http, session))
                {
                    offline = true;
                }
            }
        }

        if (!offline)
        {
            var remoteLock = await GetTerminalLockStatusAsync(http, terminalId, username, offline: false);
            if (remoteLock.IsCashierRestricted || remoteLock.IsTerminalLocked)
            {
                return new ShiftVerificationResult
                {
                    IsReady = false,
                    Offline = false,
                    Issue = ShiftVerificationIssue.Locked,
                    LockStatus = remoteLock,
                    Shift = localLatestClosedShift,
                    ErrorMessage = remoteLock.CashierRestrictionMessage ?? remoteLock.LockMessage ?? "POS Terminal is locked for the current business day."
                };
            }

            if (localOpenShift != null && localOpenShift.OpeningConfirmed)
            {
                var fetch = await FetchCurrentShiftOnlineAsync(http, terminalId);
                if (fetch.AuthoritativeRejectionStatus.HasValue)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = false,
                        Issue = fetch.AuthoritativeRejectionStatus == HttpStatusCode.Locked
                            ? ShiftVerificationIssue.Locked
                            : ShiftVerificationIssue.AuthorizationRejected,
                        LockStatus = remoteLock,
                        ErrorMessage = fetch.ErrorMessage ?? "Shift verification rejected by server."
                    };
                }

                if (fetch.UseLocalFallback)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = true,
                        Offline = true,
                        Issue = ShiftVerificationIssue.None,
                        Shift = localOpenShift,
                        LockStatus = remoteLock
                    };
                }

                if (fetch.Dto != null)
                {
                    if (fetch.Dto.BranchId != branchId)
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.BranchMismatch,
                            LockStatus = remoteLock,
                            ErrorMessage = "Server shift branch does not match the active terminal branch."
                        };
                    }

                    if (!string.Equals(fetch.Dto.CashierId, username, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.DifferentCashierOpen,
                            LockStatus = remoteLock,
                            ErrorMessage = $"This drawer is open on the server under cashier '{fetch.Dto.CashierId}'."
                        };
                    }

                    var synced = await CacheAsync(fetch.Dto, reconcileCurrent: true);
                    if (!synced.OpeningConfirmed)
                    {
                        var cfg = await GetOpeningFundConfigAsync(http, terminalId, offline: false);
                        return BuildUnconfiguredOrOpeningResult(synced, cfg, remoteLock, offline: false, ShiftVerificationIssue.OpeningNotConfirmed);
                    }

                    return new ShiftVerificationResult
                    {
                        IsReady = true,
                        Offline = false,
                        Issue = ShiftVerificationIssue.None,
                        Shift = synced,
                        LockStatus = remoteLock
                    };
                }

                if (fetch.IsNotFound)
                {
                    if (localOpenShift.ShiftId < 0)
                    {
                        var openOutcome = await TryOpenOnlineWithRetryAsync(http, BuildOpenShiftDtoFromLocal(localOpenShift), username);
                        if (openOutcome.SucceededOnline && openOutcome.Result != null)
                        {
                            return new ShiftVerificationResult
                            {
                                IsReady = true,
                                Offline = false,
                                Issue = ShiftVerificationIssue.None,
                                Shift = openOutcome.Result,
                                LockStatus = remoteLock
                            };
                        }

                        if (openOutcome.UseLocalFallback)
                        {
                            return new ShiftVerificationResult
                            {
                                IsReady = true,
                                Offline = true,
                                Issue = ShiftVerificationIssue.None,
                                Shift = localOpenShift,
                                LockStatus = remoteLock
                            };
                        }

                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = openOutcome.RejectionStatus == HttpStatusCode.Locked
                                ? ShiftVerificationIssue.Locked
                                : (openOutcome.RejectionStatus is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                                    ? ShiftVerificationIssue.AuthorizationRejected
                                    : ShiftVerificationIssue.NoActiveShift),
                            LockStatus = remoteLock,
                            ErrorMessage = openOutcome.RejectionMessage ?? "Unable to confirm offline shift opening on server."
                        };
                    }

                    await RetireStaleLocalOpenShiftsAsync(terminalId);
                    if (localClosedForCurrentDay)
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.ClosedShift,
                            Shift = localLatestClosedShift,
                            LockStatus = remoteLock,
                            ErrorMessage = "The shift on this drawer has already been closed. Reconcile in My Shift or wait until the next business day."
                        };
                    }

                    var cfgAfterRetire = await GetOpeningFundConfigAsync(http, terminalId, offline: false);
                    return BuildUnconfiguredOrOpeningResult(null, cfgAfterRetire, remoteLock, offline: false, ShiftVerificationIssue.NoActiveShift);
                }
            }
            else
            {
                var fetch = await FetchCurrentShiftOnlineAsync(http, terminalId);
                if (fetch.AuthoritativeRejectionStatus.HasValue)
                {
                    return new ShiftVerificationResult
                    {
                        IsReady = false,
                        Offline = false,
                        Issue = fetch.AuthoritativeRejectionStatus == HttpStatusCode.Locked
                            ? ShiftVerificationIssue.Locked
                            : ShiftVerificationIssue.AuthorizationRejected,
                        LockStatus = remoteLock,
                        ErrorMessage = fetch.ErrorMessage ?? "Shift verification rejected by server."
                    };
                }

                if (fetch.Dto != null)
                {
                    if (fetch.Dto.BranchId != branchId)
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.BranchMismatch,
                            LockStatus = remoteLock,
                            ErrorMessage = "Server shift branch does not match the active terminal branch."
                        };
                    }

                    if (!string.Equals(fetch.Dto.CashierId, username, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.DifferentCashierOpen,
                            LockStatus = remoteLock,
                            ErrorMessage = $"This drawer is open on the server under cashier '{fetch.Dto.CashierId}'."
                        };
                    }

                    var cached = await CacheAsync(fetch.Dto, reconcileCurrent: true);
                    if (cached.Status != "OPEN")
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.ClosedShift,
                            Shift = cached,
                            LockStatus = remoteLock,
                            ErrorMessage = "The acknowledged shift is closed. Reconcile the drawer in My Shift before opening a new shift."
                        };
                    }

                    if (!cached.OpeningConfirmed)
                    {
                        var cfgUnconfirmed = await GetOpeningFundConfigAsync(http, terminalId, offline: false);
                        return BuildUnconfiguredOrOpeningResult(cached, cfgUnconfirmed, remoteLock, offline: false, ShiftVerificationIssue.OpeningNotConfirmed);
                    }

                    return new ShiftVerificationResult
                    {
                        IsReady = true,
                        Offline = false,
                        Issue = ShiftVerificationIssue.None,
                        Shift = cached,
                        LockStatus = remoteLock
                    };
                }

                if (fetch.UseLocalFallback)
                {
                    offline = true;
                    localLock = remoteLock;
                }
                else if (fetch.IsNotFound)
                {
                    var pending = await RetireStaleLocalOpenShiftsAsync(terminalId);
                    if (pending != null)
                    {
                        localOpenShift = pending;
                        if (!pending.OpeningConfirmed)
                        {
                            var cfgPending = await GetOpeningFundConfigAsync(http, terminalId, offline: false);
                            return BuildUnconfiguredOrOpeningResult(pending, cfgPending, remoteLock, offline: false, ShiftVerificationIssue.OpeningNotConfirmed);
                        }

                        return new ShiftVerificationResult
                        {
                            IsReady = true,
                            Offline = false,
                            Issue = ShiftVerificationIssue.None,
                            Shift = pending,
                            LockStatus = remoteLock
                        };
                    }

                    if (localClosedForCurrentDay)
                    {
                        return new ShiftVerificationResult
                        {
                            IsReady = false,
                            Offline = false,
                            Issue = ShiftVerificationIssue.ClosedShift,
                            Shift = localLatestClosedShift,
                            LockStatus = remoteLock,
                            ErrorMessage = "The current business day's shift on this drawer is closed. Reconcile in My Shift or wait until the next business day."
                        };
                    }

                    var cfgNoShift = await GetOpeningFundConfigAsync(http, terminalId, offline: false);
                    return BuildUnconfiguredOrOpeningResult(null, cfgNoShift, remoteLock, offline: false, ShiftVerificationIssue.NoActiveShift);
                }
            }
        }

        // Offline / local fallback evaluation
        if (localOpenShift != null)
        {
            if (!localOpenShift.OpeningConfirmed)
            {
                var cfgLocalUnconfirmed = await GetOpeningFundConfigAsync(http, terminalId, offline: true);
                return BuildUnconfiguredOrOpeningResult(localOpenShift, cfgLocalUnconfirmed, localLock, offline: true, ShiftVerificationIssue.OpeningNotConfirmed);
            }

            return new ShiftVerificationResult
            {
                IsReady = true,
                Offline = true,
                Issue = ShiftVerificationIssue.None,
                Shift = localOpenShift,
                LockStatus = localLock
            };
        }

        if (localClosedForCurrentDay)
        {
            return new ShiftVerificationResult
            {
                IsReady = false,
                Offline = true,
                Issue = ShiftVerificationIssue.ClosedShift,
                Shift = localLatestClosedShift,
                LockStatus = localLock,
                ErrorMessage = "The current business day's shift on this drawer is already closed. Reconcile in My Shift or wait until the next business day."
            };
        }

        var localFundCfg = await GetOpeningFundConfigAsync(http, terminalId, offline: true);
        return BuildUnconfiguredOrOpeningResult(null, localFundCfg, localLock, offline: true, ShiftVerificationIssue.NoActiveShift);
    }

    private static ShiftVerificationResult BuildUnconfiguredOrOpeningResult(
        LocalCashDrawerShift? shift,
        TerminalCashFundConfigDto cfg,
        TerminalClosingLockDto lockStatus,
        bool offline,
        ShiftVerificationIssue configuredIssue)
    {
        if (!cfg.IsConfigured || !cfg.EffectiveOpeningFund.HasValue)
        {
            return new ShiftVerificationResult
            {
                IsReady = false,
                Offline = offline,
                Issue = ShiftVerificationIssue.MissingOpeningConfig,
                Shift = shift,
                LockStatus = lockStatus,
                OpeningFundConfig = cfg,
                ErrorMessage = "Initial Cash Fund is not configured for this Terminal or Outlet. Please contact an Administrator."
            };
        }

        return new ShiftVerificationResult
        {
            IsReady = false,
            Offline = offline,
            Issue = configuredIssue,
            Shift = shift,
            LockStatus = lockStatus,
            OpeningFundConfig = cfg,
            ErrorMessage = configuredIssue == ShiftVerificationIssue.OpeningNotConfirmed
                ? "Confirm the opening cash float before processing payment."
                : "No active open shift found. Open a shift and confirm the opening float before payment."
        };
    }

    public static bool IsSupervisorRole(string? role) =>
        POS.Shared.Constants.PosPermissions.IsSupervisorRole(role);

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
            var fetch = await FetchCurrentShiftOnlineAsync(http, terminalId);
            if (fetch.AuthoritativeRejectionStatus.HasValue)
                throw new InvalidOperationException(fetch.ErrorMessage ?? $"Shift verification failed with HTTP {(int)fetch.AuthoritativeRejectionStatus.Value}.");
            if (fetch.Dto != null)
                return await CacheAsync(fetch.Dto, reconcileCurrent: true);
            if (fetch.IsNotFound)
                return await RetireStaleLocalOpenShiftsAsync(terminalId);
        }

        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        return await GetLocalActiveOpenShiftAsync(local, terminalId, asNoTracking: true);
    }

    public static async Task<CashDrawerShiftDto?> CurrentDtoAsync(HttpClient http, int terminalId, bool offline, bool isSupervisor = false)
    {
        if (!offline)
        {
            var fetch = await FetchCurrentShiftOnlineAsync(http, terminalId);
            if (fetch.AuthoritativeRejectionStatus.HasValue)
                throw new InvalidOperationException(fetch.ErrorMessage ?? $"Shift verification failed with HTTP {(int)fetch.AuthoritativeRejectionStatus.Value}.");
            if (fetch.Dto != null)
            {
                var cached = await CacheAsync(fetch.Dto, reconcileCurrent: true);
                using var db = new LocalPosDbContext(); db.InitializeDatabase();
                return await BuildLocalShiftDtoAsync(db, cached, fetch.Dto, isReprint: false, includeSupervisorReconciliation: isSupervisor);
            }
            if (fetch.IsNotFound)
            {
                var pending = await RetireStaleLocalOpenShiftsAsync(terminalId);
                if (pending == null) return null;
                using var db = new LocalPosDbContext(); db.InitializeDatabase();
                return await BuildLocalShiftDtoAsync(db, pending, includeSupervisorReconciliation: isSupervisor);
            }
        }

        using var local = new LocalPosDbContext(); local.InitializeDatabase();
        var localShift = await GetLocalActiveOpenShiftAsync(local, terminalId, asNoTracking: true);
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

    public static async Task<TerminalCashFundConfigDto> GetOpeningFundConfigAsync(HttpClient http, int terminalId, bool offline)
    {
        if (!offline && terminalId > 0)
        {
            try
            {
                var response = await http.GetAsync($"api/cash-drawer/opening-fund-config/{terminalId}");
                if (response.IsSuccessStatusCode)
                {
                    var remoteCfg = await response.Content.ReadFromJsonAsync<TerminalCashFundConfigDto>();
                    if (remoteCfg != null)
                    {
                        await LocalPosDbContext.WriteLock.WaitAsync();
                        try
                        {
                            using var db = new LocalPosDbContext();
                            db.InitializeDatabase();
                            var localTerm = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == terminalId);
                            if (localTerm != null)
                            {
                                localTerm.DefaultOpeningFund = remoteCfg.DefaultOpeningFund;
                                localTerm.OpeningFundOverride = remoteCfg.OpeningFundOverride;
                                localTerm.EffectiveOpeningFund = remoteCfg.EffectiveOpeningFund;
                                localTerm.OpeningFundSource = remoteCfg.OpeningFundSource;
                                await db.SaveChangesAsync();
                            }
                        }
                        finally { LocalPosDbContext.WriteLock.Release(); }
                        return remoteCfg;
                    }
                }
            }
            catch
            {
                // Fallback to cached SQLite config when offline or unreachable
            }
        }

        using var localDb = new LocalPosDbContext();
        localDb.InitializeDatabase();
        var cached = await localDb.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                     ?? await localDb.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
        if (cached == null)
        {
            return new TerminalCashFundConfigDto { TerminalId = terminalId };
        }

        return new TerminalCashFundConfigDto
        {
            TerminalId = cached.TerminalId,
            TerminalCode = cached.TerminalCode,
            TerminalName = cached.TerminalName,
            OutletId = cached.OutletId,
            OutletName = cached.OutletName,
            BranchId = cached.BranchId,
            BranchName = cached.BranchName ?? string.Empty,
            DefaultOpeningFund = cached.DefaultOpeningFund ?? (cached.OpeningFundSource == "OUTLET_DEFAULT" ? cached.EffectiveOpeningFund : null),
            OpeningFundOverride = cached.OpeningFundOverride ?? (cached.OpeningFundSource == "TERMINAL_OVERRIDE" ? cached.EffectiveOpeningFund : null)
        };
    }

    /// <summary>
    /// Applies a Terminal-level Z-Reading finalization lock locally until 4:00 AM next Business Date.
    /// NEVER call this from Close Cashier; call ApplyLocalCashierClosingRestrictionAsync instead.
    /// </summary>
    public static void ApplyLocalClosingLock(LocalTerminalConfig? termCfg, string finalizedBy, DateTime finalizedAtUtc)
    {
        if (termCfg == null) return;
        var utcFinalized = BusinessDayClock.NormalizeUtc(finalizedAtUtc);
        termCfg.ClosedBusinessDate = BusinessDayClock.GetBusinessDateString(utcFinalized);
        termCfg.ClosingLockedByCashier = finalizedBy;
        termCfg.ClosingLockedAtUtc = utcFinalized;
        termCfg.ClosingUnlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(utcFinalized);
        termCfg.ClosingLockedTickCount64 = BusinessDayClock.TickCount64();
    }

    /// <summary>
    /// Persists a Terminal-level Z-Reading lock locally after Z-Reading finalization succeeds for the current Business Date.
    /// </summary>
    public static async Task ApplyLocalTerminalZLockAsync(
        int terminalId,
        DateOnly businessDate,
        string finalizedBy,
        DateTime finalizedAtUtc)
    {
        var nowUtc = BusinessDayClock.UtcNow();
        var unlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(businessDate);
        string dateStr = businessDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string currentBizDateStr = BusinessDayClock.GetBusinessDateString(nowUtc);
        if (string.CompareOrdinal(dateStr, currentBizDateStr) < 0 || nowUtc >= unlockAtUtc)
            return;

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            var termCfg = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                          ?? await db.TerminalConfigs.FirstOrDefaultAsync();
            if (termCfg != null)
            {
                var utcFinalized = BusinessDayClock.NormalizeUtc(finalizedAtUtc);
                termCfg.ClosedBusinessDate = dateStr;
                termCfg.ClosingLockedByCashier = finalizedBy;
                termCfg.ClosingLockedAtUtc = utcFinalized;
                termCfg.ClosingUnlockAtUtc = unlockAtUtc;
                termCfg.ClosingLockedTickCount64 = BusinessDayClock.TickCount64();
                await db.SaveChangesAsync();
            }
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    /// <summary>
    /// Persists a cashier-level Close Cashier restriction (CashierId + TerminalId + BusinessDate) locally.
    /// Does NOT lock the terminal for other cashiers and does NOT block the same cashier on other terminals.
    /// </summary>
    public static async Task ApplyLocalCashierClosingRestrictionAsync(
        LocalPosDbContext db,
        int branchId,
        int terminalId,
        string cashierId,
        long? shiftId,
        string? clientShiftId,
        string? clientCloseId,
        DateTime closedAtUtc,
        string syncStatus = "PENDING")
    {
        if (terminalId <= 0 || string.IsNullOrWhiteSpace(cashierId)) return;
        var utcClosed = BusinessDayClock.NormalizeUtc(closedAtUtc);
        var bizDate = BusinessDayClock.GetBusinessDateString(utcClosed);
        var unlockAtUtc = BusinessDayClock.GetNextBusinessDayStartUtc(utcClosed);
        string normalizedCashier = cashierId.Trim().ToLowerInvariant();

        LocalCashierClosingRestriction? existing = null;
        if (!string.IsNullOrWhiteSpace(clientCloseId))
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId && r.ClientCloseId == clientCloseId);
        }
        if (existing == null && shiftId.HasValue && shiftId.Value != 0)
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId && r.ShiftId == shiftId.Value);
        }
        if (existing == null)
        {
            existing = await db.CashierClosingRestrictions.FirstOrDefaultAsync(
                r => r.TerminalId == terminalId
                     && r.BusinessDate == bizDate
                     && r.Status == "ACTIVE"
                     && r.CashierId.ToLower() == normalizedCashier);
        }

        if (existing == null)
        {
            db.CashierClosingRestrictions.Add(new LocalCashierClosingRestriction
            {
                ClientRestrictionId = Guid.NewGuid().ToString("N"),
                BranchId = branchId,
                TerminalId = terminalId,
                CashierId = cashierId.Trim(),
                BusinessDate = bizDate,
                ShiftId = shiftId,
                ClientShiftId = clientShiftId,
                ClientCloseId = clientCloseId,
                ClosedAtUtc = utcClosed,
                UnlockAtUtc = unlockAtUtc,
                ClosedAtTickCount64 = BusinessDayClock.TickCount64(),
                Status = "ACTIVE",
                SyncStatus = syncStatus
            });
        }
        else if (existing.Status == "ACTIVE")
        {
            existing.ShiftId ??= shiftId;
            if (string.IsNullOrWhiteSpace(existing.ClientShiftId)) existing.ClientShiftId = clientShiftId;
            if (string.IsNullOrWhiteSpace(existing.ClientCloseId)) existing.ClientCloseId = clientCloseId;
            if (string.Equals(syncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase))
                existing.SyncStatus = "SYNCED";
        }
    }

    public static async Task<List<CashierClosingRestrictionDto>> GetLocalActiveCashierRestrictionsAsync(
        LocalPosDbContext db,
        int terminalId,
        DateTime effectiveNowUtc,
        long? currentTickCount64 = null)
    {
        if (terminalId <= 0) return new List<CashierClosingRestrictionDto>();
        string currentBizDate = BusinessDayClock.GetBusinessDateString(effectiveNowUtc);
        var rows = await db.CashierClosingRestrictions
            .AsNoTracking()
            .Where(r => r.TerminalId == terminalId && r.Status == "ACTIVE")
            .OrderByDescending(r => r.ClosedAtUtc)
            .ToListAsync();

        var active = new List<CashierClosingRestrictionDto>();
        foreach (var r in rows)
        {
            var closedUtc = BusinessDayClock.NormalizeUtc(r.ClosedAtUtc);
            var unlockUtc = r.UnlockAtUtc != default
                ? BusinessDayClock.NormalizeUtc(r.UnlockAtUtc)
                : BusinessDayClock.GetNextBusinessDayStartUtc(closedUtc);

            bool activeByDate = string.CompareOrdinal(r.BusinessDate, currentBizDate) >= 0 && effectiveNowUtc < unlockUtc;

            // Offline clock-rollback tamper guard using monotonic tick count
            bool activeByTamper = false;
            if (!activeByDate && r.ClosedAtTickCount64.HasValue && currentTickCount64.HasValue && currentTickCount64.Value >= r.ClosedAtTickCount64.Value)
            {
                var elapsedTicks = TimeSpan.FromMilliseconds(currentTickCount64.Value - r.ClosedAtTickCount64.Value);
                var requiredWait = unlockUtc - closedUtc;
                if (effectiveNowUtc < closedUtc.AddMinutes(-2) && elapsedTicks < requiredWait)
                    activeByTamper = true;
            }

            if (activeByDate || activeByTamper)
            {
                active.Add(new CashierClosingRestrictionDto
                {
                    RestrictionId = r.ServerRestrictionId ?? 0,
                    ClientRestrictionId = r.ClientRestrictionId,
                    BranchId = r.BranchId,
                    TerminalId = r.TerminalId,
                    CashierId = r.CashierId,
                    BusinessDate = r.BusinessDate,
                    ShiftId = r.ShiftId,
                    ClientShiftId = r.ClientShiftId,
                    ClientCloseId = r.ClientCloseId,
                    ClosedAtUtc = closedUtc,
                    UnlockAtUtc = unlockUtc,
                    Status = r.Status,
                    ReopenedBy = r.ReopenedBy,
                    ReopenedAtUtc = r.ReopenedAtUtc,
                    ReopenReason = r.ReopenReason
                });
            }
        }
        return active;
    }

    private static void ApplyActiveShiftOwnershipToLock(
        TerminalClosingLockDto status,
        string? ownerCashierId,
        long? activeShiftId,
        string? candidateCashierUsername)
    {
        if (string.IsNullOrWhiteSpace(ownerCashierId))
            return;

        status.ActiveShiftOwnerCashierId = ownerCashierId.Trim();
        status.ActiveShiftId = activeShiftId;
        if (!string.IsNullOrWhiteSpace(candidateCashierUsername) &&
            !string.Equals(ownerCashierId.Trim(), candidateCashierUsername.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            status.IsShiftOwnedByAnotherCashier = true;
            status.ActiveShiftOwnershipMessage = TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage;
        }
    }

    public static async Task<TerminalClosingLockDto> GetTerminalLockStatusAsync(
        HttpClient? http,
        int terminalId,
        string? cashierUsername = null,
        bool offline = false)
    {
        if (!offline && http != null && terminalId > 0)
        {
            try
            {
                string url = string.IsNullOrWhiteSpace(cashierUsername)
                    ? $"api/cash-drawer/terminal-lock/{terminalId}"
                    : $"api/cash-drawer/terminal-lock/{terminalId}?cashierUsername={Uri.EscapeDataString(cashierUsername.Trim())}";
                var response = await http.GetAsync(url);
                if (response.IsSuccessStatusCode)
                {
                    var remoteLock = await response.Content.ReadFromJsonAsync<TerminalClosingLockDto>();
                    if (remoteLock != null)
                    {
                        await LocalPosDbContext.WriteLock.WaitAsync();
                        try
                        {
                            using var db = new LocalPosDbContext();
                            db.InitializeDatabase();
                            var serverNowUtc = remoteLock.ServerUtcNow ?? BusinessDayClock.UtcNow();
                            var nowTick = BusinessDayClock.TickCount64();
                            var currentBizDate = BusinessDayClock.GetBusinessDateString(serverNowUtc);

                            var localTerm = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                                            ?? await db.TerminalConfigs.FirstOrDefaultAsync();
                            if (localTerm != null)
                            {
                                localTerm.LastKnownServerUtc = serverNowUtc;
                                localTerm.LastKnownServerTickCount64 = nowTick;

                                if (remoteLock.IsTerminalLocked)
                                {
                                    localTerm.ClosedBusinessDate = remoteLock.ClosedBusinessDate;
                                    localTerm.ClosingLockedByCashier = remoteLock.LockedByCashier;
                                    localTerm.ClosingLockedAtUtc = remoteLock.LockedAtUtc;
                                    localTerm.ClosingUnlockAtUtc = remoteLock.UnlockAtUtc;
                                    localTerm.ClosingLockedTickCount64 ??= nowTick;
                                }
                                else
                                {
                                    localTerm.ClosedBusinessDate = null;
                                    localTerm.ClosingLockedByCashier = null;
                                    localTerm.ClosingLockedAtUtc = null;
                                    localTerm.ClosingUnlockAtUtc = null;
                                    localTerm.ClosingLockedTickCount64 = null;
                                }
                            }

                            // Sync active cashier closing restrictions from server while preserving unsynced local restrictions/reopens
                            var localRowsToday = await db.CashierClosingRestrictions
                                .Where(r => r.TerminalId == terminalId && r.BusinessDate == currentBizDate)
                                .ToListAsync();
                            var serverRestrictions = remoteLock.ActiveCashierRestrictions ?? new List<CashierClosingRestrictionDto>();

                            foreach (var localRow in localRowsToday)
                            {
                                if (localRow.SyncStatus == "SYNCED" &&
                                    !serverRestrictions.Any(sr =>
                                        string.Equals(sr.CashierId, localRow.CashierId, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(sr.Status, "ACTIVE", StringComparison.OrdinalIgnoreCase)))
                                {
                                    localRow.Status = "REOPENED";
                                }
                            }
                            foreach (var sr in serverRestrictions)
                            {
                                var existing = localRowsToday.FirstOrDefault(l =>
                                    (sr.RestrictionId > 0 && l.ServerRestrictionId == sr.RestrictionId) ||
                                    (!string.IsNullOrWhiteSpace(sr.ClientCloseId) && l.ClientCloseId == sr.ClientCloseId) ||
                                    string.Equals(l.CashierId, sr.CashierId, StringComparison.OrdinalIgnoreCase));
                                if (existing == null)
                                {
                                    db.CashierClosingRestrictions.Add(new LocalCashierClosingRestriction
                                    {
                                        ClientRestrictionId = !string.IsNullOrWhiteSpace(sr.ClientRestrictionId) ? sr.ClientRestrictionId : Guid.NewGuid().ToString("N"),
                                        ServerRestrictionId = sr.RestrictionId > 0 ? sr.RestrictionId : null,
                                        BranchId = sr.BranchId,
                                        TerminalId = sr.TerminalId,
                                        CashierId = sr.CashierId,
                                        BusinessDate = sr.BusinessDate,
                                        ShiftId = sr.ShiftId,
                                        ClientShiftId = sr.ClientShiftId,
                                        ClientCloseId = sr.ClientCloseId,
                                        ClosedAtUtc = BusinessDayClock.NormalizeUtc(sr.ClosedAtUtc),
                                        UnlockAtUtc = BusinessDayClock.NormalizeUtc(sr.UnlockAtUtc),
                                        ClosedAtTickCount64 = nowTick,
                                        Status = sr.Status,
                                        ReopenedBy = sr.ReopenedBy,
                                        ReopenedAtUtc = sr.ReopenedAtUtc,
                                        ReopenReason = sr.ReopenReason,
                                        SyncStatus = "SYNCED"
                                    });
                                }
                                else if (existing.SyncStatus == "SYNCED")
                                {
                                    existing.ServerRestrictionId = sr.RestrictionId > 0 ? sr.RestrictionId : existing.ServerRestrictionId;
                                    existing.Status = sr.Status;
                                    existing.UnlockAtUtc = BusinessDayClock.NormalizeUtc(sr.UnlockAtUtc);
                                }
                            }

                            await db.SaveChangesAsync();

                            // Merge any pending local unsynced cashier restrictions (e.g. offline SHIFT_CLOSE awaiting upload)
                            var mergedActive = await GetLocalActiveCashierRestrictionsAsync(db, terminalId, serverNowUtc, nowTick);
                            var mergedStatus = BusinessDayClock.EvaluateLock(
                                terminalId: terminalId,
                                closedBusinessDate: remoteLock.IsTerminalLocked ? remoteLock.ClosedBusinessDate : null,
                                lockedByCashier: remoteLock.IsTerminalLocked ? remoteLock.LockedByCashier : null,
                                lockedAtUtc: remoteLock.IsTerminalLocked ? remoteLock.LockedAtUtc : null,
                                unlockAtUtc: remoteLock.IsTerminalLocked ? remoteLock.UnlockAtUtc : null,
                                candidateCashierUsername: cashierUsername,
                                authoritativeServerUtc: serverNowUtc,
                                additionalClosedCashiersToday: mergedActive.Select(r => r.CashierId));
                            mergedStatus.ActiveCashierRestrictions = mergedActive;

                            // Enforce single active open shift ownership: honor unsynced local open shift first, else server open shift
                            LocalCashDrawerShift? localOpenOnline = null;
                            try { localOpenOnline = await GetLocalActiveOpenShiftAsync(db, terminalId, asNoTracking: true); } catch { }
                            if (localOpenOnline != null && localOpenOnline.ShiftId < 0)
                            {
                                ApplyActiveShiftOwnershipToLock(mergedStatus, localOpenOnline.CashierId, localOpenOnline.ShiftId, cashierUsername);
                            }
                            else if (!string.IsNullOrWhiteSpace(remoteLock.ActiveShiftOwnerCashierId))
                            {
                                bool locallyClosedPendingUpload = remoteLock.ActiveShiftId.HasValue &&
                                    await db.CashDrawerShifts.AsNoTracking().AnyAsync(s =>
                                        s.ShiftId == remoteLock.ActiveShiftId.Value &&
                                        s.Status == "CLOSED" &&
                                        !string.IsNullOrWhiteSpace(s.ClientCloseId));
                                if (!locallyClosedPendingUpload)
                                {
                                    ApplyActiveShiftOwnershipToLock(mergedStatus, remoteLock.ActiveShiftOwnerCashierId, remoteLock.ActiveShiftId, cashierUsername);
                                }
                            }

                            return mergedStatus;
                        }
                        finally { LocalPosDbContext.WriteLock.Release(); }
                    }
                }
            }
            catch
            {
                // Fallback to local SQLite lock evaluation when offline or unreachable
            }
        }

        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var localDb = new LocalPosDbContext();
            localDb.InitializeDatabase();
            var cached = (terminalId > 0
                ? await localDb.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                : null) ?? await localDb.TerminalConfigs.FirstOrDefaultAsync();

            var nowUtc = BusinessDayClock.UtcNow();
            var nowTicks = BusinessDayClock.TickCount64();
            int effectiveTerminalId = cached?.TerminalId ?? terminalId;
            var localActiveRestrictions = await GetLocalActiveCashierRestrictionsAsync(localDb, effectiveTerminalId, nowUtc, nowTicks);
            LocalCashDrawerShift? localOpenOffline = null;
            try { localOpenOffline = await GetLocalActiveOpenShiftAsync(localDb, effectiveTerminalId, asNoTracking: true); } catch { }

            if (cached == null)
            {
                var statusNoTerm = BusinessDayClock.EvaluateLock(
                    terminalId: terminalId,
                    closedBusinessDate: null,
                    lockedByCashier: null,
                    lockedAtUtc: null,
                    unlockAtUtc: null,
                    candidateCashierUsername: cashierUsername,
                    authoritativeServerUtc: null,
                    deviceUtcNow: nowUtc,
                    currentTickCount64: nowTicks,
                    additionalClosedCashiersToday: localActiveRestrictions.Select(r => r.CashierId));
                statusNoTerm.ActiveCashierRestrictions = localActiveRestrictions;
                ApplyActiveShiftOwnershipToLock(statusNoTerm, localOpenOffline?.CashierId, localOpenOffline?.ShiftId, cashierUsername);
                return statusNoTerm;
            }

            var status = BusinessDayClock.EvaluateLock(
                terminalId: cached.TerminalId,
                closedBusinessDate: cached.ClosedBusinessDate,
                lockedByCashier: cached.ClosingLockedByCashier,
                lockedAtUtc: cached.ClosingLockedAtUtc,
                unlockAtUtc: cached.ClosingUnlockAtUtc,
                candidateCashierUsername: cashierUsername,
                authoritativeServerUtc: null,
                deviceUtcNow: nowUtc,
                currentTickCount64: nowTicks,
                lockedAtTickCount64: cached.ClosingLockedTickCount64,
                lastKnownServerUtc: cached.LastKnownServerUtc,
                lastKnownServerTickCount64: cached.LastKnownServerTickCount64,
                additionalClosedCashiersToday: localActiveRestrictions.Select(r => r.CashierId));
            status.ActiveCashierRestrictions = localActiveRestrictions;
            ApplyActiveShiftOwnershipToLock(status, localOpenOffline?.CashierId, localOpenOffline?.ShiftId, cashierUsername);

            if (!status.IsTerminalLocked && !string.IsNullOrWhiteSpace(cached.ClosedBusinessDate))
            {
                cached.ClosedBusinessDate = null;
                cached.ClosingLockedByCashier = null;
                cached.ClosingLockedAtUtc = null;
                cached.ClosingUnlockAtUtc = null;
                cached.ClosingLockedTickCount64 = null;
                await localDb.SaveChangesAsync();
            }

            return status;
        }
        finally { LocalPosDbContext.WriteLock.Release(); }
    }

    private static async Task<OnlineOperationOutcome<CashDrawerShiftDto>> PostShiftJsonWithRetryAsync<TRequest>(
        HttpClient http,
        string url,
        TRequest request,
        string actionLabel)
    {
        if (!DeviceActivationService.HasValidOnlineBearerToken(http, null))
            return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);

        AttachActiveTerminalTokenIfMissing(http);

        HttpResponseMessage? response = null;
        try
        {
            response = await http.PostAsJsonAsync(url, request);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                var refreshed = await DeviceActivationService.EnsureOnlineBearerTokenWithStatusAsync(http, null, forceRefresh: true);
                if (refreshed.RejectionStatus is HttpStatusCode.Forbidden or HttpStatusCode.Locked)
                {
                    return new OnlineOperationOutcome<CashDrawerShiftDto>(
                        false,
                        UseLocalFallback: false,
                        null,
                        refreshed.RejectionStatus,
                        BuildHttpErrorMessage($"{actionLabel} rejected", refreshed.RejectionStatus.Value, refreshed.ErrorMessage));
                }

                if (refreshed.IsTransientError)
                    return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);

                if (refreshed.Success)
                {
                    response.Dispose();
                    response = await http.PostAsJsonAsync(url, request);
                }
            }

            if (response.IsSuccessStatusCode)
            {
                var dto = await response.Content.ReadFromJsonAsync<CashDrawerShiftDto>();
                if (dto == null)
                    throw new InvalidOperationException($"{actionLabel} response was empty.");
                return new OnlineOperationOutcome<CashDrawerShiftDto>(true, UseLocalFallback: false, dto);
            }

            if (IsTransientStatusCode(response.StatusCode))
                return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);

            string errBody = await SafeReadErrorMessageAsync(response);
            return new OnlineOperationOutcome<CashDrawerShiftDto>(
                false,
                UseLocalFallback: false,
                null,
                response.StatusCode,
                string.IsNullOrWhiteSpace(errBody)
                    ? BuildHttpErrorMessage($"{actionLabel} failed", response.StatusCode, null)
                    : errBody);
        }
        catch (HttpRequestException)
        {
            return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);
        }
        catch (TaskCanceledException)
        {
            return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);
        }
        catch (TimeoutException)
        {
            return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);
        }
        catch (JsonException)
        {
            return new OnlineOperationOutcome<CashDrawerShiftDto>(false, UseLocalFallback: true, null);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<OnlineOperationOutcome<LocalCashDrawerShift>> TryOpenOnlineWithRetryAsync(
        HttpClient http,
        OpenShiftDto request,
        string cashier)
    {
        var postResult = await PostShiftJsonWithRetryAsync(http, "api/cash-drawer/open-shift", request, "Open shift");
        if (!postResult.SucceededOnline || postResult.Result == null)
            return new OnlineOperationOutcome<LocalCashDrawerShift>(false, postResult.UseLocalFallback, null, postResult.RejectionStatus, postResult.RejectionMessage);

        var cached = await CacheAsync(postResult.Result);
        return new OnlineOperationOutcome<LocalCashDrawerShift>(true, UseLocalFallback: false, cached);
    }

    public static async Task<LocalCashDrawerShift> OpenAsync(HttpClient http, OpenShiftDto request, string cashier, bool offline)
    {
        var licenseGate = await ClientLicenseService.CheckOperationalGateAsync(request.TerminalId);
        if (!licenseGate.Allowed)
            throw new InvalidOperationException(licenseGate.ErrorMessage ?? "Terminal license is not valid for opening a new shift.");

        if (request.OpeningDenominations is { Count: > 0 })
        {
            if (!CashDenominationCatalog.TryValidateAndCalculate(request.OpeningDenominations, out var normDenoms, out decimal countedOpeningCash, out string? denomErr))
                throw new InvalidOperationException(denomErr ?? "Invalid opening cash denomination breakdown.");
            request.OpeningDenominations = normDenoms;
            request.OpeningFloat = countedOpeningCash;
        }
        if (!request.OpeningFloat.HasValue || request.OpeningFloat < 0) throw new InvalidOperationException("Enter opening float. Zero is allowed.");
        if (request.ExpectedOpeningFund.HasValue && request.ExpectedOpeningFund.Value < 0m) throw new InvalidOperationException("Expected opening fund cannot be negative.");
        request.CashierId = cashier;
        if (string.IsNullOrWhiteSpace(request.ClientShiftId)) throw new InvalidOperationException("Opening identity is required.");
        if (!offline)
        {
            var openResult = await TryOpenOnlineWithRetryAsync(http, request, cashier);
            if (openResult.SucceededOnline && openResult.Result != null)
                return openResult.Result;
            if (!openResult.UseLocalFallback)
                throw new InvalidOperationException(openResult.RejectionMessage ?? "Unable to open shift on server.");
        }
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext(); db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();
            var termCfg = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == request.TerminalId && t.BranchId == request.BranchId && t.OutletId > 0);
            if (termCfg == null) throw new InvalidOperationException("Refresh terminal ownership online before opening offline.");
            var nowUtcOpen = BusinessDayClock.UtcNow();
            var nowTicksOpen = BusinessDayClock.TickCount64();
            var activeRestrictionsOpen = await GetLocalActiveCashierRestrictionsAsync(db, termCfg.TerminalId, nowUtcOpen, nowTicksOpen);
            var lockStatus = POS.Shared.Services.BusinessDayClock.EvaluateLock(
                terminalId: termCfg.TerminalId,
                closedBusinessDate: termCfg.ClosedBusinessDate,
                lockedByCashier: termCfg.ClosingLockedByCashier,
                lockedAtUtc: termCfg.ClosingLockedAtUtc,
                unlockAtUtc: termCfg.ClosingUnlockAtUtc,
                candidateCashierUsername: cashier,
                authoritativeServerUtc: null,
                deviceUtcNow: nowUtcOpen,
                currentTickCount64: nowTicksOpen,
                lockedAtTickCount64: termCfg.ClosingLockedTickCount64,
                lastKnownServerUtc: termCfg.LastKnownServerUtc,
                lastKnownServerTickCount64: termCfg.LastKnownServerTickCount64,
                additionalClosedCashiersToday: activeRestrictionsOpen.Select(r => r.CashierId));
            if (lockStatus.IsCashierRestricted || lockStatus.IsTerminalLocked)
                throw new InvalidOperationException(lockStatus.CashierRestrictionMessage ?? lockStatus.LockMessage ?? "POS Terminal is locked for the remainder of the current business day.");
            if (!string.IsNullOrWhiteSpace(termCfg.ClosedBusinessDate))
            {
                termCfg.ClosedBusinessDate = null;
                termCfg.ClosingLockedByCashier = null;
                termCfg.ClosingLockedAtUtc = null;
                termCfg.ClosingUnlockAtUtc = null;
                termCfg.ClosingLockedTickCount64 = null;
            }
            decimal? configuredFund = termCfg.OpeningFundOverride ?? termCfg.DefaultOpeningFund ?? termCfg.EffectiveOpeningFund;
            decimal? resolvedExpected = request.ExpectedOpeningFund.HasValue
                ? Math.Round(request.ExpectedOpeningFund.Value, 2, MidpointRounding.AwayFromZero)
                : configuredFund;
            if (request.RequireConfiguredFund && !resolvedExpected.HasValue)
                throw new InvalidOperationException("Initial Cash Fund is not configured for this Terminal or Outlet. Please contact an Administrator.");
            var existing = await GetLocalActiveOpenShiftAsync(db, request.TerminalId, asNoTracking: false);
            if (existing != null)
            {
                if (!string.Equals(existing.CashierId, cashier, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(TerminalClosingLockDto.ActiveShiftOwnedByAnotherCashierMessage);

                if (!string.IsNullOrWhiteSpace(request.ClientShiftId) &&
                    string.Equals(existing.ClientShiftId, request.ClientShiftId, StringComparison.Ordinal) &&
                    existing.BranchId == request.BranchId &&
                    string.Equals(existing.CashierId, cashier, StringComparison.OrdinalIgnoreCase))
                {
                    bool hasOutbox = await db.SyncOutboxes.AnyAsync(o => o.EntityType == "SHIFT_OPEN" && o.EntityId == existing.ClientShiftId);
                    if (!hasOutbox)
                    {
                        db.SyncOutboxes.Add(new LocalSyncOutbox { EntityType = "SHIFT_OPEN", EntityId = existing.ClientShiftId!, PayloadJson = JsonSerializer.Serialize(request), Status = "PENDING", CreatedAt = existing.OpenedAt });
                        await db.SaveChangesAsync();
                        await tx.CommitAsync();
                    }
                    else
                    {
                        await tx.RollbackAsync();
                    }
                    return existing;
                }
                throw new InvalidOperationException("This drawer already has an open shift.");
            }
            decimal openingFloat = Math.Round(request.OpeningFloat.Value, 2, MidpointRounding.AwayFromZero);
            decimal expectedOpening = resolvedExpected ?? openingFloat;
            decimal openingDiscrepancy = Math.Round(openingFloat - expectedOpening, 2, MidpointRounding.AwayFromZero);
            request.ExpectedOpeningFund = expectedOpening;
            request.OpeningDiscrepancy = openingDiscrepancy;
            var openedAtUtc = request.OpenedAt.HasValue
                ? (request.OpenedAt.Value.Kind == DateTimeKind.Local ? request.OpenedAt.Value.ToUniversalTime() : DateTime.SpecifyKind(request.OpenedAt.Value, DateTimeKind.Utc))
                : POS.Shared.Services.BusinessDayClock.UtcNow();
            request.OpenedAt = openedAtUtc;
            request.CashierId = cashier;
            var shift = new LocalCashDrawerShift
            {
                ShiftId = -DateTime.UtcNow.Ticks,
                BranchId = request.BranchId,
                TerminalId = request.TerminalId,
                CashierId = cashier,
                OpeningConfirmed = true,
                ClientShiftId = request.ClientShiftId,
                OpeningFloat = openingFloat,
                ExpectedOpeningFund = expectedOpening,
                OpeningDiscrepancy = openingDiscrepancy,
                OpeningDenominationsJson = request.OpeningDenominations != null ? JsonSerializer.Serialize(request.OpeningDenominations) : null,
                ExpectedBalance = openingFloat,
                OpenedAt = openedAtUtc,
                Notes = request.Notes
            };
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
        request.ClosedAt ??= POS.Shared.Services.BusinessDayClock.UtcNow();

        if (!offline)
        {
            var closeResult = await PostShiftJsonWithRetryAsync(http, "api/cash-drawer/close-shift", request, "Close shift");
            if (closeResult.SucceededOnline && closeResult.Result != null)
            {
                var serverDto = closeResult.Result;
                await CacheAsync(serverDto);
                await LocalPosDbContext.WriteLock.WaitAsync();
                try
                {
                    using var lockDb = new LocalPosDbContext(); lockDb.InitializeDatabase();
                    await ApplyLocalCashierClosingRestrictionAsync(
                        lockDb,
                        serverDto.BranchId,
                        serverDto.TerminalId,
                        serverDto.CashierId,
                        serverDto.ShiftId,
                        serverDto.ClientShiftId,
                        serverDto.ClientCloseId ?? request.ClientCloseId,
                        serverDto.ClosedAt ?? request.ClosedAt ?? BusinessDayClock.UtcNow(),
                        syncStatus: "SYNCED");
                    await lockDb.SaveChangesAsync();
                }
                finally { LocalPosDbContext.WriteLock.Release(); }
                return serverDto;
            }

            if (!closeResult.UseLocalFallback)
                throw new InvalidOperationException(closeResult.RejectionMessage ?? "Shift closing rejected by server.");
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

            var closedAtUtc = request.ClosedAt.Value >= shift.OpenedAt ? request.ClosedAt.Value : POS.Shared.Services.BusinessDayClock.UtcNow();
            shift.Status = "CLOSED";
            shift.ClosedAt = closedAtUtc;
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
                    SubmittedAt = closedAtUtc,
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
                    CreatedAt = closedAtUtc
                });
            }

            await ApplyLocalCashierClosingRestrictionAsync(
                db,
                shift.BranchId,
                shift.TerminalId,
                shift.CashierId,
                shift.ShiftId,
                shift.ClientShiftId,
                shift.ClientCloseId,
                closedAtUtc,
                syncStatus: "PENDING");

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

    /// <summary>
    /// Generates an anytime strictly read-only X-Reading audit snapshot for the current (or specified) Business Date.
    /// Works online via GET api/cash-drawer/x-reading AND offline from SQLite without mutating shifts, locks, or counters.
    /// </summary>
    public static async Task<XReadingReportDto> GetAnytimeXReadingAsync(
        HttpClient? http,
        int terminalId,
        DateOnly? businessDate = null,
        bool offline = false,
        string? supervisorPin = null,
        bool isSupervisorCaller = false,
        bool reprint = false)
    {
        if (terminalId <= 0)
            throw new InvalidOperationException("Valid terminal is required to generate an X-Reading.");

        var nowUtc = BusinessDayClock.UtcNow();
        var targetDate = businessDate ?? BusinessDayClock.GetBusinessDate(nowUtc);
        string dateStr = targetDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        if (!offline && http != null && DeviceActivationService.HasValidOnlineBearerToken(http, null))
        {
            try
            {
                AttachActiveTerminalTokenIfMissing(http);
                var queryParts = new List<string>
                {
                    $"terminalId={terminalId}",
                    $"businessDate={Uri.EscapeDataString(dateStr)}",
                    $"reprint={(reprint ? "true" : "false")}"
                };
                if (!string.IsNullOrWhiteSpace(supervisorPin))
                    queryParts.Add($"supervisorPin={Uri.EscapeDataString(supervisorPin.Trim())}");

                using var response = await http.GetAsync($"api/cash-drawer/x-reading?{string.Join("&", queryParts)}");
                if (response.IsSuccessStatusCode)
                {
                    var remote = await response.Content.ReadFromJsonAsync<XReadingReportDto>();
                    if (remote != null)
                        return remote;
                }
                else if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new InvalidOperationException("Supervisor role or valid supervisor PIN is required to generate an X-Reading.");
                }
                else if (!IsTransientStatusCode(response.StatusCode) && response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    string err = await SafeReadErrorMessageAsync(response);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(err) ? $"X-Reading request failed (HTTP {(int)response.StatusCode})." : err);
                }
            }
            catch (HttpRequestException) { /* fallback to local SQLite */ }
            catch (TaskCanceledException) { /* fallback to local SQLite */ }
            catch (TimeoutException) { /* fallback to local SQLite */ }
        }

        // Offline / local SQLite strictly read-only aggregation (ZERO writes, ZERO mutations)
        using var db = new LocalPosDbContext();
        db.InitializeDatabase();

        bool authorizedOffline = isSupervisorCaller;
        if (!authorizedOffline && !string.IsNullOrWhiteSpace(supervisorPin))
        {
            var sup = await VerifyLocalSupervisorPinAsync(db, supervisorPin);
            authorizedOffline = sup != null;
        }
        if (!authorizedOffline)
            throw new InvalidOperationException("Supervisor role or valid supervisor PIN is required to generate an X-Reading offline.");

        var termCfg = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == terminalId)
                      ?? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();

        var (periodStartUtc, periodEndUtc) = BusinessDayClock.GetPeriodUtc(targetDate);
        var effectiveEndUtc = nowUtc < periodEndUtc ? nowUtc : periodEndUtc;

        var allTerminalShifts = await db.CashDrawerShifts.AsNoTracking()
            .Where(s => s.TerminalId == terminalId)
            .OrderBy(s => s.OpenedAt)
            .ToListAsync();

        var dayShifts = allTerminalShifts.Where(s =>
        {
            var openedUtc = BusinessDayClock.NormalizeUtc(s.OpenedAt);
            var closedUtc = s.ClosedAt.HasValue ? BusinessDayClock.NormalizeUtc(s.ClosedAt.Value) : (DateTime?)null;
            return (openedUtc >= periodStartUtc && openedUtc < periodEndUtc)
                || (closedUtc.HasValue && closedUtc.Value >= periodStartUtc && closedUtc.Value < periodEndUtc)
                || (string.Equals(s.Status, "OPEN", StringComparison.OrdinalIgnoreCase) && openedUtc < periodEndUtc);
        }).ToList();

        var dayShiftIds = dayShifts.Select(s => s.ShiftId).ToHashSet();
        var dayClientShiftIds = dayShifts.Where(s => !string.IsNullOrWhiteSpace(s.ClientShiftId)).Select(s => s.ClientShiftId!).ToHashSet(StringComparer.Ordinal);

        var allOrders = await db.Orders.AsNoTracking()
            .Where(o => o.TerminalId == terminalId)
            .OrderBy(o => o.CreatedAt)
            .ToListAsync();

        var periodOrders = allOrders.Where(o =>
        {
            var createdUtc = BusinessDayClock.NormalizeUtc(o.CreatedAt);
            bool inWindow = createdUtc >= periodStartUtc && createdUtc <= effectiveEndUtc;
            bool inDayShift = (o.ShiftId.HasValue && dayShiftIds.Contains(o.ShiftId.Value))
                || (!string.IsNullOrWhiteSpace(o.ClientShiftId) && dayClientShiftIds.Contains(o.ClientShiftId));
            return inWindow || inDayShift;
        }).ToList();

        var periodOrderIds = periodOrders.Select(o => o.OrderId).ToHashSet(StringComparer.Ordinal);
        var allPayments = periodOrderIds.Count > 0
            ? await db.Payments.AsNoTracking().Where(p => periodOrderIds.Contains(p.OrderId)).ToListAsync()
            : new List<LocalPayment>();

        var paidOrders = periodOrders.Where(o => string.Equals(o.PaymentStatus, "PAID", StringComparison.OrdinalIgnoreCase) && !string.Equals(o.OrderStatus, "VOIDED", StringComparison.OrdinalIgnoreCase)).ToList();
        var voidOrders = periodOrders.Where(o => string.Equals(o.PaymentStatus, "VOIDED", StringComparison.OrdinalIgnoreCase) || string.Equals(o.OrderStatus, "VOIDED", StringComparison.OrdinalIgnoreCase)).ToList();

        decimal grossSales = Math.Round(paidOrders.Sum(o => o.Subtotal) + voidOrders.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero);
        decimal totalDiscounts = Math.Round(paidOrders.Sum(o => o.DiscountAmount), 2, MidpointRounding.AwayFromZero);
        decimal voidTotal = Math.Round(voidOrders.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero);
        decimal netSales = Math.Round(paidOrders.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero);

        var invoices = periodOrders
            .Where(o => !string.IsNullOrWhiteSpace(o.InvoiceNumber))
            .OrderBy(o => o.CreatedAt)
            .Select(o => o.InvoiceNumber)
            .ToList();

        int activeShiftCount = dayShifts.Count(s => string.Equals(s.Status, "OPEN", StringComparison.OrdinalIgnoreCase));
        int closedShiftCount = dayShifts.Count(s => string.Equals(s.Status, "CLOSED", StringComparison.OrdinalIgnoreCase));

        decimal totalOpeningFloat = Math.Round(dayShifts.Sum(s => s.OpeningFloat), 2, MidpointRounding.AwayFromZero);
        decimal totalCashSales = Math.Round(dayShifts.Sum(s => s.TotalCashSales), 2, MidpointRounding.AwayFromZero);
        decimal totalCashRefunds = Math.Round(dayShifts.Sum(s => s.TotalCashRefunds), 2, MidpointRounding.AwayFromZero);
        decimal totalCashIn = Math.Round(dayShifts.Sum(s => s.TotalCashIn), 2, MidpointRounding.AwayFromZero);
        decimal totalCashOut = Math.Round(dayShifts.Sum(s => s.TotalCashOut), 2, MidpointRounding.AwayFromZero);
        decimal totalCashDrops = Math.Round(dayShifts.Sum(s => s.TotalCashDrops), 2, MidpointRounding.AwayFromZero);
        decimal expectedCashInDrawer = Math.Round(dayShifts.Sum(s => s.ExpectedBalance), 2, MidpointRounding.AwayFromZero);
        decimal countedCashInDrawer = Math.Round(dayShifts.Where(s => s.ActualCountedBalance.HasValue).Sum(s => s.ActualCountedBalance!.Value), 2, MidpointRounding.AwayFromZero);
        decimal cashNet = Math.Round(totalCashSales - totalCashRefunds, 2, MidpointRounding.AwayFromZero);
        decimal closedExpectedCash = Math.Round(dayShifts.Where(s => string.Equals(s.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)).Sum(s => s.ExpectedBalance), 2, MidpointRounding.AwayFromZero);
        decimal cashVariance = activeShiftCount == 0 && closedShiftCount > 0
            ? Math.Round(countedCashInDrawer - closedExpectedCash, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var configuredMethods = await db.PaymentMethods.AsNoTracking().OrderBy(m => m.DisplayOrder).ThenBy(m => m.Name).ToListAsync();
        var reconciliations = new List<PaymentMethodReconciliationDto>
        {
            new()
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
            }
        };

        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CASH" };
        var nonCashCodes = new List<string>();
        foreach (var pm in configuredMethods.Where(m => m.IsActive))
            if (seenCodes.Add(pm.Code)) nonCashCodes.Add(pm.Code);
        foreach (var p in allPayments)
        {
            if (!string.IsNullOrWhiteSpace(p.PaymentMethod) && seenCodes.Add(p.PaymentMethod.Trim()))
                nonCashCodes.Add(p.PaymentMethod.Trim().ToUpperInvariant());
        }

        foreach (var code in nonCashCodes)
        {
            var cfg = configuredMethods.FirstOrDefault(m => string.Equals(m.Code, code, StringComparison.OrdinalIgnoreCase));
            decimal mGross = Math.Round(allPayments
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(p.PaymentStatus, "PAID", StringComparison.OrdinalIgnoreCase) || string.Equals(p.PaymentStatus, "REFUNDED", StringComparison.OrdinalIgnoreCase)))
                .Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            decimal mRef = Math.Round(allPayments
                .Where(p => string.Equals(p.PaymentMethod, code, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(p.PaymentStatus, "REFUNDED", StringComparison.OrdinalIgnoreCase))
                .Sum(p => p.Amount), 2, MidpointRounding.AwayFromZero);
            decimal mNet = Math.Round(mGross - mRef, 2, MidpointRounding.AwayFromZero);
            reconciliations.Add(new PaymentMethodReconciliationDto
            {
                PaymentMethodCode = cfg?.Code ?? code.ToUpperInvariant(),
                PaymentMethodName = cfg?.Name ?? code.ToUpperInvariant(),
                IsCash = false,
                IsActive = cfg?.IsActive ?? false,
                DisplayOrder = cfg?.DisplayOrder ?? 999,
                GrossSalesAmount = mGross,
                RefundAmount = mRef,
                NetSalesAmount = mNet,
                ExpectedAmount = mNet,
                DeclaredAmount = 0m,
                Variance = 0m,
                Status = activeShiftCount > 0 ? "IN_PROGRESS" : "READ_ONLY"
            });
        }

        var cashierNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in dayShifts) if (!string.IsNullOrWhiteSpace(s.CashierId)) cashierNames.Add(s.CashierId.Trim());
        foreach (var o in periodOrders) if (!string.IsNullOrWhiteSpace(o.CashierId)) cashierNames.Add(o.CashierId.Trim());

        var cashierSummaries = new List<XReadingCashierSummaryDto>();
        foreach (var cashier in cashierNames.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            var cShifts = dayShifts.Where(s => string.Equals(s.CashierId, cashier, StringComparison.OrdinalIgnoreCase)).ToList();
            var cOrders = periodOrders.Where(o => string.Equals(o.CashierId, cashier, StringComparison.OrdinalIgnoreCase)).ToList();
            var cPaid = cOrders.Where(o => string.Equals(o.PaymentStatus, "PAID", StringComparison.OrdinalIgnoreCase) && !string.Equals(o.OrderStatus, "VOIDED", StringComparison.OrdinalIgnoreCase)).ToList();
            var cVoid = cOrders.Where(o => string.Equals(o.PaymentStatus, "VOIDED", StringComparison.OrdinalIgnoreCase) || string.Equals(o.OrderStatus, "VOIDED", StringComparison.OrdinalIgnoreCase)).ToList();
            cashierSummaries.Add(new XReadingCashierSummaryDto
            {
                CashierId = cashier,
                ShiftCount = cShifts.Count,
                OpenShifts = cShifts.Count(s => string.Equals(s.Status, "OPEN", StringComparison.OrdinalIgnoreCase)),
                ClosedShifts = cShifts.Count(s => string.Equals(s.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)),
                TransactionCount = cPaid.Count,
                GrossSales = Math.Round(cPaid.Sum(o => o.Subtotal) + cVoid.Sum(o => o.Subtotal), 2, MidpointRounding.AwayFromZero),
                NetSales = Math.Round(cPaid.Sum(o => o.TotalAmount), 2, MidpointRounding.AwayFromZero)
            });
        }

        var openShift = dayShifts.LastOrDefault(s => string.Equals(s.Status, "OPEN", StringComparison.OrdinalIgnoreCase));
        return new XReadingReportDto
        {
            ShiftId = openShift?.ShiftId ?? dayShifts.LastOrDefault()?.ShiftId ?? 0,
            ClientShiftId = openShift?.ClientShiftId ?? dayShifts.LastOrDefault()?.ClientShiftId,
            BranchId = termCfg?.BranchId ?? 0,
            BranchName = termCfg?.BranchName ?? "Main Branch",
            BranchCode = termCfg?.BranchCode,
            BranchAddress = termCfg?.BranchAddress,
            BranchPhone = termCfg?.BranchPhone,
            OutletId = termCfg?.OutletId ?? 0,
            OutletName = termCfg?.OutletName ?? string.Empty,
            TerminalId = terminalId,
            TerminalCode = termCfg?.TerminalCode ?? string.Empty,
            TerminalName = termCfg?.TerminalName ?? $"Terminal #{terminalId}",
            PTUNumber = termCfg?.PTUNumber,
            MINNumber = termCfg?.MINNumber,
            SerialNumber = termCfg?.SerialNumber,
            CashierId = activeShiftCount > 0
                ? (openShift?.CashierId ?? "ACTIVE SHIFT")
                : (cashierSummaries.Count == 1 ? cashierSummaries[0].CashierId : "ALL CASHIERS"),
            OpenedAt = dayShifts.FirstOrDefault()?.OpenedAt ?? periodStartUtc,
            ClosedAt = activeShiftCount == 0 ? dayShifts.LastOrDefault()?.ClosedAt : null,
            GeneratedAt = nowUtc,
            BusinessDate = dateStr,
            IsAnytimeAudit = true,
            ActiveShiftCount = activeShiftCount,
            ClosedShiftCount = closedShiftCount,
            CashierSummaries = cashierSummaries,
            IsReprint = reprint,
            ReportingPeriodStart = periodStartUtc,
            ReportingPeriodEnd = effectiveEndUtc,
            ReportingPeriodMatchesShift = dayShifts.Count == 1,
            BeginningInvoiceNumber = invoices.FirstOrDefault(),
            EndingInvoiceNumber = invoices.LastOrDefault(),
            CompletedTransactionsCount = paidOrders.Count,
            VoidedTransactionsCount = voidOrders.Count,
            ReturnTransactionsCount = 0,
            GrossSales = grossSales,
            TotalDiscounts = totalDiscounts,
            TotalRefundsAndVoids = voidTotal,
            NetSales = netSales,
            VatableSales = Math.Round(paidOrders.Sum(o => o.VatableSales), 2, MidpointRounding.AwayFromZero),
            VatExemptSales = Math.Round(paidOrders.Sum(o => o.VatExemptSales), 2, MidpointRounding.AwayFromZero),
            ZeroRatedSales = Math.Round(paidOrders.Sum(o => o.ZeroRatedSales), 2, MidpointRounding.AwayFromZero),
            VatAmount = Math.Round(paidOrders.Sum(o => o.VatAmount), 2, MidpointRounding.AwayFromZero),
            ShiftTransactionsCount = paidOrders.Count,
            ShiftGrossSales = grossSales,
            ShiftDiscounts = totalDiscounts,
            ShiftRefundsAndVoids = voidTotal,
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
    }

    /// <summary>
    /// Authorized supervisor reopen workflow allowing a closed cashier to sign back in on the same terminal/Business Date.
    /// Preserves all closed CashDrawerShift records immutably and queues idempotent offline sync when disconnected.
    /// </summary>
    public static async Task<CashierClosingRestrictionDto> ReopenCashierRestrictionAsync(
        HttpClient? http,
        ReopenCashierRestrictionRequestDto request,
        string callerUsername,
        string? callerRole,
        bool offline)
    {
        if (request == null || request.TerminalId <= 0)
            throw new InvalidOperationException("Valid terminal is required.");
        if (string.IsNullOrWhiteSpace(request.CashierId))
            throw new InvalidOperationException("Cashier username is required.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            throw new InvalidOperationException("A reason is required to reopen cashier access.");

        var nowUtc = BusinessDayClock.UtcNow();
        request.ClientOperationId = string.IsNullOrWhiteSpace(request.ClientOperationId)
            ? $"REOPEN-{Guid.NewGuid():N}"
            : request.ClientOperationId.Trim();
        request.BusinessDate = string.IsNullOrWhiteSpace(request.BusinessDate)
            ? BusinessDayClock.GetBusinessDateString(nowUtc)
            : request.BusinessDate.Trim();
        request.CashierId = request.CashierId.Trim();
        request.Reason = request.Reason.Trim();

        if (!offline && http != null && DeviceActivationService.HasValidOnlineBearerToken(http, null))
        {
            try
            {
                AttachActiveTerminalTokenIfMissing(http);
                using var response = await http.PostAsJsonAsync("api/cash-drawer/cashier-restrictions/reopen", request);
                if (response.IsSuccessStatusCode)
                {
                    var dto = await response.Content.ReadFromJsonAsync<CashierClosingRestrictionDto>();
                    if (dto != null)
                    {
                        await MarkLocalCashierReopenedAsync(
                            request.TerminalId,
                            request.CashierId,
                            request.BusinessDate,
                            dto.ReopenedBy ?? callerUsername,
                            request.Reason,
                            request.ClientOperationId,
                            syncStatus: "SYNCED",
                            queueOutboxPayload: null);
                        return dto;
                    }
                }
                else if (!IsTransientStatusCode(response.StatusCode) && response.StatusCode != HttpStatusCode.Unauthorized)
                {
                    string err = await SafeReadErrorMessageAsync(response);
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                        ? $"Failed to reopen cashier access (HTTP {(int)response.StatusCode})."
                        : err);
                }
            }
            catch (HttpRequestException) { /* fallback to offline */ }
            catch (TaskCanceledException) { /* fallback to offline */ }
            catch (TimeoutException) { /* fallback to offline */ }
        }

        // Offline supervisor verification (never persist plaintext PIN in outbox)
        string? approverUsername = null;
        string? approverPinHash = null;
        using (var authDb = new LocalPosDbContext())
        {
            authDb.InitializeDatabase();
            if (!string.IsNullOrWhiteSpace(request.SupervisorPin))
            {
                var sup = await VerifyLocalSupervisorPinAsync(authDb, request.SupervisorPin);
                if (sup == null)
                    throw new InvalidOperationException("Invalid supervisor PIN or user does not have supervisor privileges.");
                approverUsername = sup.Username;
                approverPinHash = sup.PinHash;
            }
            else if (IsSupervisorRole(callerRole))
            {
                var localSup = await authDb.Cashiers.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Username.ToLower() == callerUsername.Trim().ToLower());
                approverUsername = callerUsername.Trim();
                approverPinHash = localSup?.PinHash;
                if (string.IsNullOrWhiteSpace(approverPinHash))
                    throw new InvalidOperationException("Enter your supervisor PIN to authorize offline cashier reopening.");
            }
            else
            {
                throw new InvalidOperationException("Supervisor authorization (role or supervisor PIN) is required to reopen cashier access.");
            }
        }

        var outboxRequest = new ReopenCashierRestrictionRequestDto
        {
            ClientOperationId = request.ClientOperationId,
            TerminalId = request.TerminalId,
            CashierId = request.CashierId,
            BusinessDate = request.BusinessDate,
            Reason = request.Reason,
            SupervisorPin = null,
            SupervisorPinHash = approverPinHash,
            ApprovedBy = approverUsername
        };

        return await MarkLocalCashierReopenedAsync(
            request.TerminalId,
            request.CashierId,
            request.BusinessDate,
            approverUsername!,
            request.Reason,
            request.ClientOperationId,
            syncStatus: "PENDING",
            queueOutboxPayload: outboxRequest);
    }

    private static async Task<CashierClosingRestrictionDto> MarkLocalCashierReopenedAsync(
        int terminalId,
        string cashierId,
        string businessDate,
        string reopenedBy,
        string reason,
        string clientOperationId,
        string syncStatus,
        ReopenCashierRestrictionRequestDto? queueOutboxPayload)
    {
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            using var tx = await db.Database.BeginTransactionAsync();

            var nowUtc = BusinessDayClock.UtcNow();
            string normCashier = cashierId.Trim().ToLowerInvariant();
            var activeRows = await db.CashierClosingRestrictions
                .Where(r => r.TerminalId == terminalId
                         && r.BusinessDate == businessDate
                         && r.Status == "ACTIVE"
                         && r.CashierId.ToLower() == normCashier)
                .ToListAsync();

            if (activeRows.Count == 0 && queueOutboxPayload != null)
            {
                var alreadyReopened = await db.CashierClosingRestrictions
                    .FirstOrDefaultAsync(r => r.TerminalId == terminalId && r.ClientOperationId == clientOperationId);
                if (alreadyReopened != null)
                {
                    await tx.RollbackAsync();
                    return new CashierClosingRestrictionDto
                    {
                        RestrictionId = alreadyReopened.ServerRestrictionId ?? 0,
                        ClientRestrictionId = alreadyReopened.ClientRestrictionId,
                        BranchId = alreadyReopened.BranchId,
                        TerminalId = alreadyReopened.TerminalId,
                        CashierId = alreadyReopened.CashierId,
                        BusinessDate = alreadyReopened.BusinessDate,
                        ShiftId = alreadyReopened.ShiftId,
                        ClientShiftId = alreadyReopened.ClientShiftId,
                        ClientCloseId = alreadyReopened.ClientCloseId,
                        ClosedAtUtc = alreadyReopened.ClosedAtUtc,
                        UnlockAtUtc = alreadyReopened.UnlockAtUtc,
                        Status = alreadyReopened.Status,
                        ReopenedBy = alreadyReopened.ReopenedBy,
                        ReopenedAtUtc = alreadyReopened.ReopenedAtUtc,
                        ReopenReason = alreadyReopened.ReopenReason
                    };
                }
                throw new InvalidOperationException("No active cashier closing restriction found locally for this cashier, terminal, and business date.");
            }

            foreach (var row in activeRows)
            {
                row.Status = "REOPENED";
                row.ReopenedBy = reopenedBy;
                row.ReopenedAtUtc = nowUtc;
                row.ReopenReason = reason;
                row.ClientOperationId = clientOperationId;
                row.SyncStatus = syncStatus;
            }

            if (queueOutboxPayload != null)
            {
                bool alreadyQueued = await db.SyncOutboxes.AnyAsync(o => o.EntityType == "CASHIER_REOPEN" && o.EntityId == clientOperationId);
                if (!alreadyQueued)
                {
                    db.SyncOutboxes.Add(new LocalSyncOutbox
                    {
                        EntityType = "CASHIER_REOPEN",
                        EntityId = clientOperationId,
                        PayloadJson = JsonSerializer.Serialize(queueOutboxPayload),
                        Status = "PENDING",
                        CreatedAt = nowUtc
                    });
                }
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            var first = activeRows.FirstOrDefault();
            return new CashierClosingRestrictionDto
            {
                RestrictionId = first?.ServerRestrictionId ?? 0,
                ClientRestrictionId = first?.ClientRestrictionId,
                BranchId = first?.BranchId ?? 0,
                TerminalId = terminalId,
                CashierId = cashierId,
                BusinessDate = businessDate,
                ShiftId = first?.ShiftId,
                ClientShiftId = first?.ClientShiftId,
                ClientCloseId = first?.ClientCloseId,
                ClosedAtUtc = first?.ClosedAtUtc ?? nowUtc,
                UnlockAtUtc = first?.UnlockAtUtc ?? BusinessDayClock.GetNextBusinessDayStartUtc(nowUtc),
                Status = "REOPENED",
                ReopenedBy = reopenedBy,
                ReopenedAtUtc = nowUtc,
                ReopenReason = reason
            };
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

        bool preserveLocalPositiveClose = shift.ShiftId > 0
            && string.Equals(shift.Status, "CLOSED", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(shift.ClientCloseId)
            && string.IsNullOrWhiteSpace(dto.ClientCloseId)
            && !string.Equals(dto.Status, "CLOSED", StringComparison.OrdinalIgnoreCase);

        // Historical opening ACKs never retire a different opening. Only an authoritative
        // current endpoint can retire other acknowledged shifts on this terminal.
        var matchingNegatives = !string.IsNullOrWhiteSpace(dto.ClientShiftId)
            ? await db.CashDrawerShifts
                .Where(s => s.TerminalId == dto.TerminalId && s.ShiftId < 0 && s.ShiftId != dto.ShiftId && s.ClientShiftId == dto.ClientShiftId)
                .ToListAsync()
            : new List<LocalCashDrawerShift>();

        var closedLocalPlaceholder = matchingNegatives
            .FirstOrDefault(s => s.Status == "CLOSED" && !string.IsNullOrWhiteSpace(s.ClientCloseId));

        var migratedMovements = new List<LocalCashDrawerMovement>();
        foreach (var neg in matchingNegatives)
        {
            if (neg.Status == "OPEN")
                neg.Status = "CLOSED";

            if (dto.ShiftId > 0 && neg.ShiftId != dto.ShiftId)
            {
                var negMovements = await db.CashDrawerMovements.Where(m => m.ShiftId == neg.ShiftId).ToListAsync();
                foreach (var mov in negMovements)
                {
                    mov.ShiftId = dto.ShiftId;
                    migratedMovements.Add(mov);
                }

                var negOrders = await db.Orders.Where(o => o.ShiftId == neg.ShiftId).ToListAsync();
                foreach (var ord in negOrders)
                    ord.ShiftId = dto.ShiftId;
            }
        }

        foreach (var old in await db.CashDrawerShifts.Where(s => s.TerminalId == dto.TerminalId && s.ShiftId != dto.ShiftId && s.Status == "OPEN").ToListAsync())
            if ((old.ShiftId < 0 && !string.IsNullOrWhiteSpace(dto.ClientShiftId) && old.ClientShiftId == dto.ClientShiftId)
                || (reconcileCurrent && old.ShiftId > 0)) old.Status = "CLOSED";

        shift.BranchId = dto.BranchId; shift.TerminalId = dto.TerminalId; shift.CashierId = dto.CashierId;
        shift.OpeningFloat = dto.OpeningFloat;
        shift.ExpectedOpeningFund = dto.ExpectedOpeningFund ?? shift.ExpectedOpeningFund ?? dto.OpeningFloat;
        shift.OpeningDiscrepancy = dto.OpeningDiscrepancy ?? Math.Round(dto.OpeningFloat - (shift.ExpectedOpeningFund ?? dto.OpeningFloat), 2, MidpointRounding.AwayFromZero);
        if (dto.OpeningDenominations != null && dto.OpeningDenominations.Count > 0)
            shift.OpeningDenominationsJson = JsonSerializer.Serialize(dto.OpeningDenominations);
        shift.OpeningConfirmed = dto.OpeningConfirmed; shift.ClientShiftId = dto.ClientShiftId;

        if (!preserveLocalPositiveClose)
        {
            if (!string.IsNullOrWhiteSpace(dto.ClientCloseId)) shift.ClientCloseId = dto.ClientCloseId;
            else if (closedLocalPlaceholder != null && !string.IsNullOrWhiteSpace(closedLocalPlaceholder.ClientCloseId)) shift.ClientCloseId = closedLocalPlaceholder.ClientCloseId;
            shift.OpenedAt = dto.OpenedAt;
            shift.ClosedAt = dto.ClosedAt ?? closedLocalPlaceholder?.ClosedAt;
            shift.Status = closedLocalPlaceholder != null ? "CLOSED" : dto.Status;
            if (dto.Notes != null) shift.Notes = dto.Notes;
            else if (closedLocalPlaceholder?.Notes != null) shift.Notes = closedLocalPlaceholder.Notes;
        }
        else
        {
            shift.OpenedAt = dto.OpenedAt;
        }

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

        var existingMovementsForShift = await db.CashDrawerMovements.AsNoTracking()
            .Where(m => m.ShiftId == dto.ShiftId)
            .ToListAsync();
        var serverMovementIds = (dto.Movements ?? new List<CashMovementResponseDto>())
            .Select(m => m.ClientMovementId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        var seenMovementKeys = new HashSet<string>(StringComparer.Ordinal);
        decimal unsyncedCashIn = 0m;
        decimal unsyncedCashOut = 0m;
        decimal unsyncedCashDrops = 0m;

        foreach (var mov in migratedMovements.Concat(existingMovementsForShift))
        {
            string key = !string.IsNullOrWhiteSpace(mov.ClientMovementId)
                ? $"C:{mov.ClientMovementId}"
                : $"M:{mov.MovementId}";
            if (!seenMovementKeys.Add(key)) continue;
            if (!string.Equals(mov.Status, "POSTED", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(mov.SyncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(mov.ClientMovementId) && serverMovementIds.Contains(mov.ClientMovementId)) continue;

            string movType = (mov.MovementType ?? string.Empty).Trim().ToUpperInvariant();
            if (movType == "CASH_IN") unsyncedCashIn += mov.Amount;
            else if (movType is "CASH_OUT" or "PETTY_CASH") unsyncedCashOut += mov.Amount;
            else if (movType == "CASH_DROP") unsyncedCashDrops += mov.Amount;
        }

        shift.TotalCashIn = dto.TotalCashIn + unsyncedCashIn;
        shift.TotalCashOut = dto.TotalCashOut + unsyncedCashOut;
        shift.TotalCashDrops = dto.TotalCashDrops + unsyncedCashDrops;
        shift.ExpectedBalance = shift.OpeningFloat + shift.TotalCashSales - shift.TotalCashRefunds + shift.TotalCashIn - shift.TotalCashOut - shift.TotalCashDrops;
        if (dto.ActualCountedBalance.HasValue)
        {
            shift.ActualCountedBalance = dto.ActualCountedBalance;
            shift.Discrepancy = Math.Round(dto.ActualCountedBalance.Value - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
        }
        else if (preserveLocalPositiveClose && shift.ActualCountedBalance.HasValue)
        {
            shift.Discrepancy = Math.Round(shift.ActualCountedBalance.Value - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
        }
        else if (closedLocalPlaceholder?.ActualCountedBalance != null)
        {
            shift.ActualCountedBalance = closedLocalPlaceholder.ActualCountedBalance;
            shift.Discrepancy = Math.Round(closedLocalPlaceholder.ActualCountedBalance.Value - shift.ExpectedBalance, 2, MidpointRounding.AwayFromZero);
        }
        if (!preserveLocalPositiveClose)
        {
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
        }
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
                            && (p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid || p.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Refunded))
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

    private static CashMovementResponseDto MapLocalMovementToDto(LocalCashDrawerMovement m) => new()
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
    };

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

        List<CashMovementResponseDto> movements;
        if (serverSnapshot?.Movements != null && serverSnapshot.Movements.Count > 0)
        {
            movements = new List<CashMovementResponseDto>(serverSnapshot.Movements);
            var serverIds = serverSnapshot.Movements
                .Select(m => m.ClientMovementId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var localMov in localMovements)
            {
                if (!string.Equals(localMov.SyncStatus, "SYNCED", StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(localMov.ClientMovementId) || !serverIds.Contains(localMov.ClientMovementId)))
                {
                    movements.Add(MapLocalMovementToDto(localMov));
                }
            }
            movements = movements.OrderByDescending(m => m.CreatedAt).ToList();
        }
        else
        {
            movements = localMovements.Select(MapLocalMovementToDto).ToList();
        }

        List<CashDenominationLineDto> openingDenoms = new();
        if (!string.IsNullOrWhiteSpace(shift.OpeningDenominationsJson))
        {
            try { openingDenoms = JsonSerializer.Deserialize<List<CashDenominationLineDto>>(shift.OpeningDenominationsJson) ?? new(); }
            catch { openingDenoms = new(); }
        }
        else if (serverSnapshot?.OpeningDenominations != null && serverSnapshot.OpeningDenominations.Count > 0)
        {
            openingDenoms = serverSnapshot.OpeningDenominations;
        }
        decimal expectedOpening = shift.ExpectedOpeningFund ?? serverSnapshot?.ExpectedOpeningFund ?? shift.OpeningFloat;
        decimal openingDiscrepancy = shift.OpeningDiscrepancy
            ?? serverSnapshot?.OpeningDiscrepancy
            ?? Math.Round(shift.OpeningFloat - expectedOpening, 2, MidpointRounding.AwayFromZero);

        var dto = new CashDrawerShiftDto
        {
            ShiftId = shift.ShiftId,
            BranchId = shift.BranchId,
            TerminalId = shift.TerminalId,
            CashierId = shift.CashierId,
            OpeningFloat = shift.OpeningFloat,
            ExpectedOpeningFund = expectedOpening,
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
            .Where(o => o.TerminalId == shift.TerminalId && o.BranchId == shift.BranchId && o.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid)
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
                                  && payment.PaymentMethod == "CASH" && payment.PaymentStatus == POS.Shared.Constants.PaymentStatuses.Paid
                              select payment).ToListAsync();
        return payments.Where(p => pendingIds.Contains(p.OrderId)).Sum(p => p.Amount);
    }
}
