using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface ILicensingApiService
{
    Task<List<TerminalLicenseAdminDto>> GetLicensesAsync(int? branchId = null, int? outletId = null, int? terminalId = null, string? status = null);
    Task<TerminalLicenseAdminDto?> GetLicenseByTerminalIdAsync(int terminalId);
    Task<LicenseOperationResponseDto> IssueLicenseAsync(IssueTerminalLicenseRequestDto request);
    Task<LicenseOperationResponseDto> RenewLicenseAsync(RenewTerminalLicenseRequestDto request);
    Task<LicenseOperationResponseDto> AdjustLicenseAsync(RenewTerminalLicenseRequestDto request);
    Task<LicenseOperationResponseDto> SuspendLicenseAsync(ChangeLicenseStatusRequestDto request);
    Task<LicenseOperationResponseDto> ReactivateLicenseAsync(ChangeLicenseStatusRequestDto request);
    Task<LicenseOperationResponseDto> RevokeLicenseAsync(ChangeLicenseStatusRequestDto request);
    Task<LicenseOperationResponseDto> DeactivateDeviceAsync(ChangeLicenseStatusRequestDto request);
    Task<LicenseOperationResponseDto> ReplaceDeviceAsync(ReplaceLicenseDeviceRequestDto request);
    Task<LicenseOperationResponseDto> ActivateTerminalLicenseAsync(LicenseActivateRequestDto request);
    Task<LicenseOperationResponseDto> ValidateTerminalLicenseAsync(LicenseValidateRequestDto request);
    Task<List<LicenseRenewalHistoryDto>> GetRenewalHistoryAsync(int? terminalId = null);
    Task<List<LicenseDeviceHistoryDto>> GetDeviceHistoryAsync(int? terminalId = null);
    Task<List<LicenseAuditLogDto>> GetAuditLogsAsync(int? terminalId = null, int limit = 100);
    Task<List<ClientLicenseSummaryDto>> GetClientLicenseSummaryAsync();
}
