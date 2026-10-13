using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading.Tasks;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class PaymentService(HttpClient http) : IPaymentService
{
    public async Task<List<PaymentMethodDto>> GetPaymentMethodsAsync()
    {
        return await http.GetFromJsonAsync<List<PaymentMethodDto>>("api/payment-methods") ?? new();
    }

    public async Task<PaymentResponseDto?> ProcessPaymentAsync(PaymentProcessDto dto)
    {
        using var response = await http.PostAsJsonAsync("api/payments", dto);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<PaymentResponseDto>();
    }
}
