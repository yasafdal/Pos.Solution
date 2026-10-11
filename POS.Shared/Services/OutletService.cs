using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;
namespace POS.Shared.Services;
public class OutletService(HttpClient http) : IOutletService
{
    public async Task<TerminalOutletDto?> ResolveTerminalAsync(string ipAddress, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/outlets/resolve-terminal?ipAddress={Uri.EscapeDataString(ipAddress)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException((await response.Content.ReadAsStringAsync()).Trim('"'));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TerminalOutletDto>()
            ?? throw new HttpRequestException("Empty outlet response.");
    }
}
