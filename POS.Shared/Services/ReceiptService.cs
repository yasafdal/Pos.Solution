using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class ReceiptService(HttpClient http) : IReceiptService
{
    public async Task<ReceiptDto?> GetReceiptAsync(long orderId, bool isReprint = false)
    {
        string url = isReprint ? $"api/orders/{orderId}/receipt?reprint=true" : $"api/orders/{orderId}/receipt";
        using var response = await http.GetAsync(url);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException("A receipt is only available for a fully paid order with successful payments.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ReceiptDto>()
            ?? throw new JsonException("The receipt response did not contain a receipt.");
    }
}
