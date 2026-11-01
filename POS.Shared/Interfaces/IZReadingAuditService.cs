using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IZReadingAuditService
{
    Task<ZReadingHistoryResponseDto> GetHistoryAsync(int? branchId = null, int? terminalId = null, DateOnly? fromDate = null, DateOnly? toDate = null, int page = 1, int pageSize = 20);
    Task<ZReadingReportDto?> GetReportByIdAsync(Guid id);
    Task<List<ZReadingPrintAttemptDto>> GetPrintAttemptsAsync(Guid reportId);
}
