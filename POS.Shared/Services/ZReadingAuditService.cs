using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class ZReadingAuditService(HttpClient http) : IZReadingAuditService
{
    public async Task<ZReadingHistoryResponseDto> GetHistoryAsync(int? branchId = null, int? terminalId = null, DateOnly? fromDate = null, DateOnly? toDate = null, int page = 1, int pageSize = 20)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (branchId.HasValue) query.Add($"branchId={branchId.Value}");
        if (terminalId.HasValue) query.Add($"terminalId={terminalId.Value}");
        if (fromDate.HasValue) query.Add($"fromDate={fromDate.Value:yyyy-MM-dd}");
        if (toDate.HasValue) query.Add($"toDate={toDate.Value:yyyy-MM-dd}");

        var url = $"api/z-readings/history?{string.Join("&", query)}";
        return await http.GetFromJsonAsync<ZReadingHistoryResponseDto>(url) ?? new();
    }

    public async Task<ZReadingReportDto?> GetReportByIdAsync(Guid id)
    {
        return await http.GetFromJsonAsync<ZReadingReportDto>($"api/z-readings/{id}");
    }

    public async Task<List<ZReadingPrintAttemptDto>> GetPrintAttemptsAsync(Guid reportId)
    {
        return await http.GetFromJsonAsync<List<ZReadingPrintAttemptDto>>($"api/z-readings/{reportId}/print-attempts") ?? new();
    }
}
