using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class OrderService(HttpClient http) : IOrderService
{
    public async Task<OrderResponseDto?> CreateOrderAsync(OrderCreateDto dto)
    {
        using var response = await http.PostAsJsonAsync("api/orders", dto);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<OrderResponseDto>();
    }

    public async Task<OrderResponseDto?> GetOrderByIdAsync(long orderId)
    {
        return await http.GetFromJsonAsync<OrderResponseDto>($"api/orders/{orderId}");
    }
}
