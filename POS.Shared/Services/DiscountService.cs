using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public class DiscountService : IDiscountService
{
    private readonly HttpClient _http;

    public DiscountService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<DiscountTypeDto>> GetDiscountTypesAsync()
    {
        return await _http.GetFromJsonAsync<List<DiscountTypeDto>>("api/discounts") ?? new List<DiscountTypeDto>();
    }

    public async Task<DiscountTypeDto> CreateDiscountAsync(DiscountTypeDto dto)
    {
        var response = await _http.PostAsJsonAsync("api/discounts/create-discount", dto);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DiscountTypeDto>() ?? new DiscountTypeDto();
    }

    public async Task UpdateDiscountAsync(int id, DiscountTypeDto dto)
    {
        var response = await _http.PutAsJsonAsync($"api/discounts/update-discount/{id}", dto);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteDiscountAsync(int id)
    {
        var response = await _http.DeleteAsync($"api/discounts/remove-discount/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task<OrderCalculationResultDto> CalculateOrderAsync(OrderCalculationRequestDto request)
    {
        var response = await _http.PostAsJsonAsync("api/discounts/calculate", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OrderCalculationResultDto>() ?? new OrderCalculationResultDto();
    }

    public async Task<List<DiscountAuditLogDto>> GetAuditLogsAsync(long? orderId = null)
    {
        string url = orderId.HasValue ? $"api/discounts/audit?orderId={orderId.Value}" : "api/discounts/audit";
        return await _http.GetFromJsonAsync<List<DiscountAuditLogDto>>(url) ?? new List<DiscountAuditLogDto>();
    }
}
