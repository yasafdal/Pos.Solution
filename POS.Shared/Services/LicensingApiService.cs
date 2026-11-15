using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class LicensingApiService(HttpClient http) : ILicensingApiService
{
    public async Task<List<TerminalLicenseAdminDto>> GetLicensesAsync(int? branchId = null, int? outletId = null, int? terminalId = null, string? status = null)
    {
        var query = new List<string>();
        if (branchId.HasValue && branchId.Value > 0) query.Add($"branchId={branchId.Value}");
        if (outletId.HasValue && outletId.Value > 0) query.Add($"outletId={outletId.Value}");
        if (terminalId.HasValue && terminalId.Value > 0) query.Add($"terminalId={terminalId.Value}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        var url = query.Count > 0 ? $"api/licensing?{string.Join("&", query)}" : "api/licensing";
        return await http.GetFromJsonAsync<List<TerminalLicenseAdminDto>>(url) ?? new();
    }

    public async Task<TerminalLicenseAdminDto?> GetLicenseByTerminalIdAsync(int terminalId)
    {
        var response = await http.GetAsync($"api/licensing/terminal/{terminalId}");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TerminalLicenseAdminDto>();
    }

    public Task<LicenseOperationResponseDto> IssueLicenseAsync(IssueTerminalLicenseRequestDto request) =>
        PostOperationAsync("api/licensing/issue", request);

    public Task<LicenseOperationResponseDto> RenewLicenseAsync(RenewTerminalLicenseRequestDto request) =>
        PostOperationAsync("api/licensing/renew", request);

    public Task<LicenseOperationResponseDto> AdjustLicenseAsync(RenewTerminalLicenseRequestDto request) =>
        PostOperationAsync("api/licensing/adjust", request);

    public Task<LicenseOperationResponseDto> SuspendLicenseAsync(ChangeLicenseStatusRequestDto request) =>
        PostOperationAsync("api/licensing/suspend", request);

    public Task<LicenseOperationResponseDto> ReactivateLicenseAsync(ChangeLicenseStatusRequestDto request) =>
        PostOperationAsync("api/licensing/reactivate", request);

    public Task<LicenseOperationResponseDto> RevokeLicenseAsync(ChangeLicenseStatusRequestDto request) =>
        PostOperationAsync("api/licensing/revoke", request);

    public Task<LicenseOperationResponseDto> DeactivateDeviceAsync(ChangeLicenseStatusRequestDto request) =>
        PostOperationAsync("api/licensing/deactivate-device", request);

    public Task<LicenseOperationResponseDto> ReplaceDeviceAsync(ReplaceLicenseDeviceRequestDto request) =>
        PostOperationAsync("api/licensing/replace-device", request);

    public Task<LicenseOperationResponseDto> ActivateTerminalLicenseAsync(LicenseActivateRequestDto request) =>
        PostOperationAsync("api/licensing/activate", request);

    public Task<LicenseOperationResponseDto> ValidateTerminalLicenseAsync(LicenseValidateRequestDto request) =>
        PostOperationAsync("api/licensing/validate", request);

    public async Task<List<LicenseRenewalHistoryDto>> GetRenewalHistoryAsync(int? terminalId = null)
    {
        var url = terminalId.HasValue && terminalId.Value > 0
            ? $"api/licensing/history/renewals?terminalId={terminalId.Value}"
            : "api/licensing/history/renewals";
        return await http.GetFromJsonAsync<List<LicenseRenewalHistoryDto>>(url) ?? new();
    }

    public async Task<List<LicenseDeviceHistoryDto>> GetDeviceHistoryAsync(int? terminalId = null)
    {
        var url = terminalId.HasValue && terminalId.Value > 0
            ? $"api/licensing/history/devices?terminalId={terminalId.Value}"
            : "api/licensing/history/devices";
        return await http.GetFromJsonAsync<List<LicenseDeviceHistoryDto>>(url) ?? new();
    }

    public async Task<List<LicenseAuditLogDto>> GetAuditLogsAsync(int? terminalId = null, int limit = 100)
    {
        var query = new List<string> { $"limit={Math.Clamp(limit, 1, 500)}" };
        if (terminalId.HasValue && terminalId.Value > 0) query.Add($"terminalId={terminalId.Value}");
        return await http.GetFromJsonAsync<List<LicenseAuditLogDto>>($"api/licensing/audit-logs?{string.Join("&", query)}") ?? new();
    }

    private async Task<LicenseOperationResponseDto> PostOperationAsync<TReq>(string url, TReq payload)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(url, payload);
            var dto = await response.Content.ReadFromJsonAsync<LicenseOperationResponseDto>();
            if (dto != null) return dto;
            return new LicenseOperationResponseDto
            {
                Success = response.IsSuccessStatusCode,
                Message = $"Server returned {(int)response.StatusCode} {response.ReasonPhrase}"
            };
        }
        catch (Exception ex)
        {
            return new LicenseOperationResponseDto
            {
                Success = false,
                ErrorCode = "NETWORK_ERROR",
                Message = $"Unable to reach licensing server: {ex.Message}"
            };
        }
    }
}
