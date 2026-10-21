using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class OutletService(HttpClient http) : IOutletService
{
    public async Task<List<OutletDto>> GetOutletsAsync(int? branchId = null)
    {
        var url = branchId.HasValue ? $"api/outlets?branchId={branchId.Value}" : "api/outlets";
        return await http.GetFromJsonAsync<List<OutletDto>>(url) ?? new();
    }

    public async Task<OutletDto?> GetOutletByIdAsync(int id)
    {
        return await http.GetFromJsonAsync<OutletDto>($"api/outlets/{id}");
    }

    public async Task<bool> CreateOutletAsync(OutletDto outlet)
    {
        var response = await http.PostAsJsonAsync("api/outlets", outlet);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateOutletAsync(int id, OutletDto outlet)
    {
        var response = await http.PutAsJsonAsync($"api/outlets/{id}", outlet);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteOutletAsync(int id)
    {
        var response = await http.DeleteAsync($"api/outlets/{id}");
        return response.IsSuccessStatusCode;
    }
}
