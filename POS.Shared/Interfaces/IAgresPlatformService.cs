using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IAgresPlatformService
{
    Task<List<OrganizationDto>> GetOrganizationsAsync();
    Task<(bool Success, string? ErrorMessage)> CreateOrganizationAsync(CreateOrganizationDto dto);
    Task<(bool Success, string? ErrorMessage)> UpdateOrganizationAsync(int organizationId, CreateOrganizationDto dto);
    Task<List<AgresSystemAdminDto>> GetSystemAdminsAsync();
    Task<(bool Success, string? ErrorMessage)> CreateSystemAdminAsync(CreateAgresSystemAdminDto dto);
    Task<(bool Success, string? ErrorMessage)> UpdateSystemAdminAsync(int systemAdminId, UpdateAgresSystemAdminDto dto);
    Task<List<PlatformAuditLogDto>> GetAuditLogsAsync(int limit = 100);
}
