using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;
namespace POS.Shared.Services;

public class ServiceModeService(HttpClient http) : IServiceModeService
{
    public async Task<List<ServiceModeDto>> GetServiceModesAsync() =>
        await http.GetFromJsonAsync<List<ServiceModeDto>>("api/ServiceModes") ?? new();
    public Task<ServiceModeDto?> GetServiceModeByIdAsync(int id) =>
        http.GetFromJsonAsync<ServiceModeDto>($"api/ServiceModes/{id}");
    public async Task<bool> CreateServiceModeAsync(ServiceModeDto item)
    {
        using var response = await http.PostAsJsonAsync("api/ServiceModes", item);
        return response.IsSuccessStatusCode;
    }
    public async Task<bool> UpdateServiceModeAsync(int id, ServiceModeDto item)
    {
        using var response = await http.PutAsJsonAsync($"api/ServiceModes/{id}", item);
        return response.IsSuccessStatusCode;
    }
    public async Task<bool> DeleteServiceModeAsync(int id)
    {
        using var response = await http.DeleteAsync($"api/ServiceModes/{id}");
        return response.IsSuccessStatusCode;
    }
}
