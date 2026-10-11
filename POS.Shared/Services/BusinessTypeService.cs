using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;
namespace POS.Shared.Services;

public class BusinessTypeService(HttpClient http) : IBusinessTypeService
{
    public async Task<List<BusinessTypeDto>> GetBusinessTypesAsync() =>
        await http.GetFromJsonAsync<List<BusinessTypeDto>>("api/BusinessTypes") ?? new();
    public Task<BusinessTypeDto?> GetBusinessTypeByIdAsync(int id) =>
        http.GetFromJsonAsync<BusinessTypeDto>($"api/BusinessTypes/{id}");
    public async Task<bool> CreateBusinessTypeAsync(BusinessTypeDto item)
    {
        using var response = await http.PostAsJsonAsync("api/BusinessTypes", item);
        return response.IsSuccessStatusCode;
    }
    public async Task<bool> UpdateBusinessTypeAsync(int id, BusinessTypeDto item)
    {
        using var response = await http.PutAsJsonAsync($"api/BusinessTypes/{id}", item);
        return response.IsSuccessStatusCode;
    }
    public async Task<bool> DeleteBusinessTypeAsync(int id)
    {
        using var response = await http.DeleteAsync($"api/BusinessTypes/{id}");
        return response.IsSuccessStatusCode;
    }
}
