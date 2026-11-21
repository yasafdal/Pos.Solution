using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class AgresPlatformService(HttpClient http) : IAgresPlatformService
{
    public async Task<List<OrganizationDto>> GetOrganizationsAsync() =>
        await http.GetFromJsonAsync<List<OrganizationDto>>("api/agres/organizations") ?? new();

    public async Task<(bool Success, string? ErrorMessage)> CreateOrganizationAsync(CreateOrganizationDto dto)
    {
        var resp = await http.PostAsJsonAsync("api/agres/organizations", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, string.IsNullOrWhiteSpace(err) ? "Failed to create organization." : err.Trim('"'));
    }

    public async Task<(bool Success, string? ErrorMessage)> UpdateOrganizationAsync(int organizationId, CreateOrganizationDto dto)
    {
        var resp = await http.PutAsJsonAsync($"api/agres/organizations/{organizationId}", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, string.IsNullOrWhiteSpace(err) ? "Failed to update organization." : err.Trim('"'));
    }

    public async Task<List<AgresSystemAdminDto>> GetSystemAdminsAsync() =>
        await http.GetFromJsonAsync<List<AgresSystemAdminDto>>("api/agres/admins") ?? new();

    public async Task<(bool Success, string? ErrorMessage)> CreateSystemAdminAsync(CreateAgresSystemAdminDto dto)
    {
        var resp = await http.PostAsJsonAsync("api/agres/admins", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, string.IsNullOrWhiteSpace(err) ? "Failed to create system admin." : err.Trim('"'));
    }

    public async Task<(bool Success, string? ErrorMessage)> UpdateSystemAdminAsync(int systemAdminId, UpdateAgresSystemAdminDto dto)
    {
        var resp = await http.PutAsJsonAsync($"api/agres/admins/{systemAdminId}", dto);
        if (resp.IsSuccessStatusCode) return (true, null);
        var err = await resp.Content.ReadAsStringAsync();
        return (false, string.IsNullOrWhiteSpace(err) ? "Failed to update system admin." : err.Trim('"'));
    }

    public async Task<List<PlatformAuditLogDto>> GetAuditLogsAsync(int limit = 100) =>
        await http.GetFromJsonAsync<List<PlatformAuditLogDto>>($"api/agres/audit-logs?limit={limit}") ?? new();
}
