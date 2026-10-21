using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class HeldSaleService(HttpClient http) : IHeldSaleService
{
    public async Task<List<HeldSaleDto>> GetHeldSalesAsync(int? branchId = null, int? terminalId = null)
    {
        var url = "api/held-sales?";
        if (branchId.HasValue) url += $"branchId={branchId}&";
        if (terminalId.HasValue) url += $"terminalId={terminalId}&";

        return await http.GetFromJsonAsync<List<HeldSaleDto>>(url) ?? new();
    }

    public async Task<HeldSaleDto?> GetHeldSaleByIdAsync(long id)
    {
        return await http.GetFromJsonAsync<HeldSaleDto>($"api/held-sales/{id}");
    }

    public async Task<HeldSaleDto?> CreateHeldSaleAsync(HeldSaleCreateDto dto)
    {
        using var response = await http.PostAsJsonAsync("api/held-sales", dto);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<HeldSaleDto>();
    }

    public async Task<HeldSaleDto?> ResumeHeldSaleAsync(long id, int? terminalId = null)
    {
        var url = $"api/held-sales/{id}/resume";
        if (terminalId.HasValue) url += $"?terminalId={terminalId.Value}";
        using var response = await http.PostAsync(url, null);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<HeldSaleDto>();
    }

    public async Task<bool> CancelHeldSaleAsync(long id)
    {
        using var response = await http.PostAsync($"api/held-sales/{id}/cancel", null);
        return response.IsSuccessStatusCode;
    }
}
