using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using POS.Client.Data;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Client.Services;

public readonly record struct BearerTokenResult(
    bool Success,
    HttpStatusCode? RejectionStatus = null,
    string? ErrorMessage = null,
    bool IsTransientError = false);

public interface IDeviceActivationService
{
    Task<bool> IsDeviceProvisionedAsync();
    Task<LocalTerminalConfig?> GetProvisionedTerminalAsync();
    Task<TerminalActivationResponseDto> ActivateDeviceAsync(string activationCode);
}

public class DeviceActivationService : IDeviceActivationService
{
    public static string? ActiveTerminalToken { get; set; }
    private readonly ITerminalService _terminalService;
    private readonly IReferenceDataSyncService? _referenceDataSync;
    private readonly HttpClient? _httpClient;

    public DeviceActivationService(
        ITerminalService terminalService,
        IReferenceDataSyncService? referenceDataSync = null,
        HttpClient? httpClient = null)
    {
        _terminalService = terminalService;
        _referenceDataSync = referenceDataSync;
        _httpClient = httpClient;
    }

    public async Task<bool> IsDeviceProvisionedAsync()
    {
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            return await db.TerminalConfigs.AsNoTracking()
                .AnyAsync(t => t.IsActivated || !string.IsNullOrEmpty(t.DeviceUuid) || t.IsInitialized);
        }
        catch
        {
            return false;
        }
    }

    public async Task<LocalTerminalConfig?> GetProvisionedTerminalAsync()
    {
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();
            return await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
        }
        catch
        {
            return null;
        }
    }

    public async Task<TerminalActivationResponseDto> ActivateDeviceAsync(string activationCode)
    {
        if (string.IsNullOrWhiteSpace(activationCode) || activationCode.Trim().Length != 6)
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = "Please enter a valid 6-digit Activation Code."
            };
        }

        string cleanCode = activationCode.Trim();

        // 1. Generate or retrieve permanent client-side Device UUID
        string deviceUuid;
        using (var dbCheck = new LocalPosDbContext())
        {
            dbCheck.InitializeDatabase();
            var existing = await dbCheck.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            deviceUuid = !string.IsNullOrWhiteSpace(existing?.DeviceUuid)
                ? existing.DeviceUuid!
                : Guid.NewGuid().ToString();
        }

        // 2. Send ActivationCode + Client-Generated DeviceUUID to backend API
        TerminalActivationResponseDto? response;
        try
        {
            response = await _terminalService.ActivateTerminalAsync(new TerminalActivationRequestDto
            {
                ActivationCode = cleanCode,
                DeviceUuid = deviceUuid,
                DeviceId = POS.Shared.Helpers.DeviceBindingHelper.ComputeDeviceId(deviceUuid)
            });
        }
        catch (Exception ex)
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = $"Unable to reach activation server: {ex.Message}"
            };
        }

        if (response == null || !response.Success || (response.Config == null && response.Terminal == null))
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = response?.Message ?? "Activation failed. Please verify the 6-digit code and try again."
            };
        }

        var cfg = response.Config;
        var term = response.Terminal;

        int terminalId = cfg?.TerminalId ?? term?.TerminalId ?? 0;
        int outletId = cfg?.OutletId ?? term?.OutletId ?? 0;
        int branchId = cfg?.BranchId ?? term?.BranchId ?? 0;
        string terminalCode = cfg?.TerminalCode ?? term?.TerminalCode ?? string.Empty;
        string terminalName = cfg?.TerminalName ?? term?.Name ?? string.Empty;
        string outletName = cfg?.OutletName ?? term?.OutletName ?? string.Empty;
        string? branchName = cfg?.BranchName ?? term?.BranchName;
        string? branchCode = cfg?.BranchCode;
        string? ptuNumber = cfg?.PTUNumber ?? term?.PTUNumber;
        string? minNumber = cfg?.MINNumber ?? term?.MINNumber;
        string? serialNumber = cfg?.SerialNumber ?? term?.SerialNumber;
        string? ipAddress = cfg?.IpAddress ?? term?.IpAddress;
        bool allowSync = cfg?.AllowSync ?? term?.AllowSync ?? true;
        var menuMode = cfg?.MenuDisplayMode ?? term?.MenuDisplayMode ?? MenuDisplayMode.TextOnly;

        // 3. Save BIR details, Terminal Code, and DeviceUUID to local SQLite and mark as Provisioned/Activated
        await LocalPosDbContext.WriteLock.WaitAsync();
        try
        {
            using var db = new LocalPosDbContext();
            db.InitializeDatabase();

            var priorPrinter = await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalBranches WHERE BranchId != {0};", branchId);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalTerminalConfigs WHERE TerminalId != {0};", terminalId);
            db.ChangeTracker.Clear();

            string resolvedBranchCode = branchCode ?? $"BR-{branchId:D2}";
            string resolvedBranchTaxType = !string.IsNullOrWhiteSpace(cfg?.BranchTaxType) ? cfg.BranchTaxType : "VAT-Registered";
            bool resolvedVatDisplay = cfg?.VatDisplay ?? true;

            if (branchId > 0)
            {
                var branchEntity = await db.Branches.FirstOrDefaultAsync(b => b.BranchId == branchId);
                if (branchEntity == null)
                {
                    branchEntity = new LocalBranch { BranchId = branchId };
                    db.Branches.Add(branchEntity);
                }
                branchEntity.Name = branchName ?? terminalName;
                branchEntity.BranchTagline = cfg?.BranchTagline;
                branchEntity.Code = resolvedBranchCode;
                branchEntity.Address = cfg?.BranchAddress;
                branchEntity.Phone = cfg?.BranchPhone;
                branchEntity.TaxType = resolvedBranchTaxType;
                branchEntity.VatDisplay = resolvedVatDisplay;
                branchEntity.BusinessTypeId = cfg?.BusinessTypeId ?? 1;
                branchEntity.BusinessType = cfg?.BusinessType ?? "FNB";
                branchEntity.ReceiptLogoUrl = cfg?.ReceiptLogoUrl;
                branchEntity.ReceiptLogoDataUri = cfg?.ReceiptLogoDataUri;
                branchEntity.ReceiptLogoUpdatedAt = cfg?.ReceiptLogoUpdatedAt;
                branchEntity.IsActive = true;
                branchEntity.UpdatedAt = DateTime.UtcNow;
            }

            var termConfigEntity = await db.TerminalConfigs.FirstOrDefaultAsync(t => t.TerminalId == terminalId);
            if (termConfigEntity == null)
            {
                termConfigEntity = new LocalTerminalConfig
                {
                    TerminalId = terminalId,
                    ReceiptPrinterName = priorPrinter?.ReceiptPrinterName,
                    KitchenPrinterName = priorPrinter?.KitchenPrinterName,
                    BarPrinterName = priorPrinter?.BarPrinterName,
                    IsInitialized = priorPrinter?.IsInitialized ?? false
                };
                db.TerminalConfigs.Add(termConfigEntity);
            }
            termConfigEntity.OutletId = outletId;
            termConfigEntity.OutletName = outletName;
            termConfigEntity.BranchId = branchId;
            termConfigEntity.BranchName = branchName;
            termConfigEntity.BranchTagline = cfg?.BranchTagline;
            termConfigEntity.BranchCode = resolvedBranchCode;
            termConfigEntity.BranchAddress = cfg?.BranchAddress;
            termConfigEntity.BranchPhone = cfg?.BranchPhone;
            termConfigEntity.ReceiptLogoUrl = cfg?.ReceiptLogoUrl;
            termConfigEntity.ReceiptLogoDataUri = cfg?.ReceiptLogoDataUri;
            termConfigEntity.ReceiptLogoUpdatedAt = cfg?.ReceiptLogoUpdatedAt;
            termConfigEntity.VatDisplay = resolvedVatDisplay;
            termConfigEntity.BusinessTypeId = cfg?.BusinessTypeId ?? 1;
            termConfigEntity.BusinessType = cfg?.BusinessType ?? "FNB";
            termConfigEntity.TerminalCode = terminalCode;
            termConfigEntity.TerminalName = terminalName;
            termConfigEntity.PTUNumber = ptuNumber;
            termConfigEntity.MINNumber = minNumber;
            termConfigEntity.SerialNumber = serialNumber;
            termConfigEntity.IpAddress = ipAddress;
            termConfigEntity.TerminalIdentifier = deviceUuid;
            termConfigEntity.DeviceUuid = deviceUuid;
            termConfigEntity.IsActivated = true;
            termConfigEntity.AllowSync = allowSync;
            termConfigEntity.ActivatedAt = DateTime.UtcNow;
            termConfigEntity.MenuDisplayMode = menuMode == MenuDisplayMode.ImageAndText
                ? MenuDisplayMode.ImageAndText : MenuDisplayMode.TextOnly;
            termConfigEntity.BranchTaxType = resolvedBranchTaxType;
            termConfigEntity.DefaultOpeningFund = cfg?.DefaultOpeningFund ?? term?.OutletDefaultOpeningFund;
            termConfigEntity.OpeningFundOverride = cfg?.OpeningFundOverride ?? term?.OpeningFundOverride;
            termConfigEntity.EffectiveOpeningFund = cfg?.EffectiveOpeningFund ?? term?.EffectiveOpeningFund;
            termConfigEntity.OpeningFundSource = cfg?.OpeningFundSource ?? term?.OpeningFundSource;
            termConfigEntity.ClosedBusinessDate = cfg?.ClosedBusinessDate ?? term?.ClosedBusinessDate ?? priorPrinter?.ClosedBusinessDate;
            termConfigEntity.ClosingLockedByCashier = cfg?.ClosingLockedByCashier ?? term?.ClosingLockedByCashier ?? priorPrinter?.ClosingLockedByCashier;
            termConfigEntity.ClosingLockedAtUtc = cfg?.ClosingLockedAtUtc ?? term?.ClosingLockedAtUtc ?? priorPrinter?.ClosingLockedAtUtc;
            termConfigEntity.ClosingUnlockAtUtc = cfg?.ClosingUnlockAtUtc ?? term?.ClosingUnlockAtUtc ?? priorPrinter?.ClosingUnlockAtUtc;
            termConfigEntity.ClosingLockedTickCount64 = priorPrinter?.ClosingLockedTickCount64;
            termConfigEntity.LastKnownServerUtc = DateTime.UtcNow;
            termConfigEntity.LastKnownServerTickCount64 = POS.Shared.Services.BusinessDayClock.TickCount64();

            if (response.Cashiers != null && response.Cashiers.Count > 0)
            {
                await db.Database.ExecuteSqlRawAsync("DELETE FROM LocalCashiers;");
                foreach (var cashier in response.Cashiers)
                {
                    db.Cashiers.Add(new LocalCashier
                    {
                        UserId = cashier.UserId,
                        Username = cashier.Username,
                        Role = cashier.Role,
                        PinHash = cashier.PinHash
                    });
                }
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            LocalPosDbContext.WriteLock.Release();
        }

        if (response.LicensePackage != null)
        {
            try
            {
                var clientLic = new ClientLicenseService();
                await clientLic.SaveSignedLicensePackageAsync(response.LicensePackage);
            }
            catch { }
        }

        // 4. Persist terminal token for offline-login sessions and run initial reference data sync
        if (!string.IsNullOrEmpty(response.Token))
        {
            ActiveTerminalToken = response.Token;
            if (_httpClient != null)
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", response.Token);
                if (_referenceDataSync != null && terminalId > 0)
                {
                    try
                    {
                        await _referenceDataSync.SyncReferenceDataAsync(terminalId, _httpClient);
                    }
                    catch
                    {
                        // Non-fatal: cashier can still sync or initialize later
                    }
                }
            }
        }

        LocalDatabaseBackupService.TriggerBackgroundSurvivableBackup(forceImmediate: true);
        return response;
    }

    public static bool HasValidOnlineBearerToken(HttpClient? httpClient, PosSessionService? session)
    {
        string? expectedUsername = session?.Username;
        if (TryGetValidToken(session?.Token, expectedUsername) != null)
            return true;
        if (TryGetValidToken(ActiveTerminalToken, expectedUsername) != null)
            return true;

        var authHeader = httpClient?.DefaultRequestHeaders.Authorization;
        if (authHeader != null &&
            (string.IsNullOrWhiteSpace(authHeader.Scheme) || string.Equals(authHeader.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)) &&
            TryGetValidToken(authHeader.Parameter, expectedUsername) != null)
        {
            return true;
        }

        return false;
    }

    private static string? TryGetValidToken(string? token, string? expectedUsername)
    {
        if (string.IsNullOrWhiteSpace(token) ||
            string.Equals(token.Trim(), "OFFLINE-SESSION-TOKEN", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return TryValidateTokenIdentity(token, expectedUsername) ? token : null;
    }

    public static async Task<bool> EnsureOnlineBearerTokenAsync(HttpClient httpClient, PosSessionService? session = null, bool forceRefresh = false)
    {
        var result = await EnsureOnlineBearerTokenWithStatusAsync(httpClient, session, forceRefresh);
        return result.Success;
    }

    public static async Task<BearerTokenResult> EnsureOnlineBearerTokenWithStatusAsync(
        HttpClient httpClient,
        PosSessionService? session = null,
        bool forceRefresh = false)
    {
        if (httpClient == null)
            return new BearerTokenResult(false, ErrorMessage: "HTTP client is unavailable.");

        if (!forceRefresh && session != null &&
            TryGetValidToken(session.Token, session.Username) is string validSessionToken &&
            TokenHasCashierIdentity(validSessionToken, session.Username))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", validSessionToken);
            return new BearerTokenResult(true);
        }

        string? previousHeaderToken = httpClient.DefaultRequestHeaders.Authorization?.Parameter;
        HttpStatusCode? rejectionStatus = null;
        string? rejectionMessage = null;
        bool isTransientError = false;

        try
        {
            LocalTerminalConfig? localOutlet;
            using (var db = new LocalPosDbContext())
            {
                db.InitializeDatabase();
                int? scopedTerminalId = session?.Terminal?.TerminalId > 0 ? session.Terminal.TerminalId : null;
                localOutlet = scopedTerminalId.HasValue
                    ? await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync(t => t.TerminalId == scopedTerminalId.Value)
                    : await db.TerminalConfigs.AsNoTracking().FirstOrDefaultAsync();
            }

            string? deviceUuid = !string.IsNullOrWhiteSpace(localOutlet?.DeviceUuid)
                ? localOutlet!.DeviceUuid
                : localOutlet?.TerminalIdentifier;

            if (localOutlet != null && !string.IsNullOrWhiteSpace(deviceUuid))
            {
                var req = new TerminalSessionTokenRequestDto
                {
                    TerminalId = localOutlet.TerminalId,
                    DeviceUuid = deviceUuid,
                    CashierUsername = session?.Username
                };
                using var resp = await System.Net.Http.Json.HttpClientJsonExtensions.PostAsJsonAsync(httpClient, "api/terminals/session-token", req);
                if (resp.IsSuccessStatusCode)
                {
                    var dto = await System.Net.Http.Json.HttpContentJsonExtensions.ReadFromJsonAsync<TerminalSessionTokenResponseDto>(resp.Content);
                    if (!string.IsNullOrWhiteSpace(dto?.Token))
                    {
                        ActiveTerminalToken = dto.Token;
                        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dto.Token);
                        if (session != null && session.Terminal != null)
                        {
                            // Preserve an existing valid non-expired cashier JWT in Session; upgrade bare terminal/offline tokens
                            if (TryGetValidToken(session.Token, session.Username) is string existingValidSessionToken &&
                                TokenHasCashierIdentity(existingValidSessionToken, session.Username))
                            {
                                httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", existingValidSessionToken);
                            }
                            else
                            {
                                session.SetSession(dto.Token, session.Username ?? string.Empty, session.Role ?? "Cashier", session.Terminal);
                            }
                        }
                        return new BearerTokenResult(true);
                    }

                    isTransientError = true;
                    rejectionStatus = resp.StatusCode;
                    rejectionMessage = "Empty session token returned by server.";
                }
                else
                {
                    string rawError = string.Empty;
                    try { rawError = await resp.Content.ReadAsStringAsync(); } catch { }
                    rejectionStatus = resp.StatusCode;
                    rejectionMessage = string.IsNullOrWhiteSpace(rawError)
                        ? $"Session token request rejected with HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase})."
                        : rawError.Trim();

                    if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Locked or HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
                    {
                        if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Locked)
                        {
                            ActiveTerminalToken = null;
                            httpClient.DefaultRequestHeaders.Authorization = null;
                        }
                        return new BearerTokenResult(false, rejectionStatus, rejectionMessage, IsTransientError: false);
                    }

                    isTransientError = true;
                }
            }
        }
        catch (HttpRequestException ex)
        {
            isTransientError = true;
            rejectionMessage = ex.Message;
        }
        catch (TaskCanceledException ex)
        {
            isTransientError = true;
            rejectionMessage = ex.Message;
        }
        catch (TimeoutException ex)
        {
            isTransientError = true;
            rejectionMessage = ex.Message;
        }
        catch (Exception ex)
        {
            isTransientError = true;
            rejectionMessage = ex.Message;
        }

        if (TryGetValidToken(ActiveTerminalToken, session?.Username) is string cachedToken &&
            (!forceRefresh || !string.Equals(cachedToken, previousHeaderToken, StringComparison.Ordinal)))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cachedToken);
            if (session != null && session.Terminal != null && (string.IsNullOrWhiteSpace(session.Token) || session.Token == "OFFLINE-SESSION-TOKEN"))
            {
                session.SetSession(cachedToken, session.Username ?? string.Empty, session.Role ?? "Cashier", session.Terminal);
            }
            return new BearerTokenResult(true);
        }

        if (!forceRefresh &&
            TryGetValidToken(httpClient.DefaultRequestHeaders.Authorization?.Parameter, session?.Username) is not null)
        {
            return new BearerTokenResult(true);
        }

        return new BearerTokenResult(false, rejectionStatus, rejectionMessage, isTransientError);
    }

    private static bool TryValidateTokenIdentity(string token, string? expectedUsername)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return true; // Non-JWT token string

            string payload = parts[1];
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expSeconds))
            {
                var expDate = DateTimeOffset.FromUnixTimeSeconds(expSeconds).UtcDateTime;
                if (expDate < DateTime.UtcNow) return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedUsername))
            {
                if (root.TryGetProperty("cashier_username", out var cuProp))
                {
                    var cu = cuProp.GetString();
                    if (!string.IsNullOrWhiteSpace(cu) && !string.Equals(cu, expectedUsername, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                string? name = null;
                if (root.TryGetProperty("unique_name", out var unProp)) name = unProp.GetString();
                else if (root.TryGetProperty("name", out var nProp)) name = nProp.GetString();
                else if (root.TryGetProperty("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", out var soapProp)) name = soapProp.GetString();

                string? role = null;
                if (root.TryGetProperty("role", out var rProp) && rProp.ValueKind == JsonValueKind.String) role = rProp.GetString();
                else if (root.TryGetProperty("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", out var soapRProp) && soapRProp.ValueKind == JsonValueKind.String) role = soapRProp.GetString();

                if (!string.Equals(role, "Terminal", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(name) &&
                    !string.Equals(name, expectedUsername, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TokenHasCashierIdentity(string token, string? expectedUsername)
    {
        if (string.IsNullOrWhiteSpace(expectedUsername))
            return true;

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return false;

            string payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;

            if (root.TryGetProperty("cashier_username", out var cuProp) &&
                string.Equals(cuProp.GetString(), expectedUsername, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string? name = null;
            if (root.TryGetProperty("unique_name", out var unProp)) name = unProp.GetString();
            else if (root.TryGetProperty("name", out var nProp)) name = nProp.GetString();
            else if (root.TryGetProperty("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", out var soapProp)) name = soapProp.GetString();

            return !string.IsNullOrWhiteSpace(name) &&
                   string.Equals(name, expectedUsername, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
