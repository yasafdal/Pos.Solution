using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;
namespace POS.Shared.Services;
public class TerminalService(HttpClient http) : ITerminalService
{
    public async Task<TerminalConfigDto?> ResolveTerminalAsync(string ipAddress, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"api/terminals/resolve-terminal?ipAddress={Uri.EscapeDataString(ipAddress)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw new InvalidOperationException((await response.Content.ReadAsStringAsync()).Trim('"'));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TerminalConfigDto>()
            ?? throw new HttpRequestException("Empty outlet response.");
    }

    public async Task<List<TerminalDto>> GetTerminalsAsync(int? outletId = null, int? branchId = null)
    {
        var query = new List<string>();
        if (outletId.HasValue) query.Add($"outletId={outletId.Value}");
        if (branchId.HasValue) query.Add($"branchId={branchId.Value}");
        var url = query.Count > 0 ? $"api/terminals?{string.Join("&", query)}" : "api/terminals";
        return await http.GetFromJsonAsync<List<TerminalDto>>(url) ?? new();
    }

    public async Task<TerminalDto?> GetTerminalByIdAsync(int id)
    {
        return await http.GetFromJsonAsync<TerminalDto>($"api/terminals/{id}");
    }

    public async Task<bool> CreateTerminalAsync(TerminalDto terminal)
    {
        var response = await http.PostAsJsonAsync("api/terminals", terminal);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateTerminalAsync(int id, TerminalDto terminal)
    {
        var response = await http.PutAsJsonAsync($"api/terminals/{id}", terminal);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteTerminalAsync(int id)
    {
        var response = await http.DeleteAsync($"api/terminals/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<List<InvoiceSequenceDto>> GetInvoiceSequencesAsync(int terminalId)
    {
        return await http.GetFromJsonAsync<List<InvoiceSequenceDto>>($"api/terminals/{terminalId}/invoice-sequences") ?? new();
    }

    public async Task<bool> CreateInvoiceSequenceAsync(int terminalId, InvoiceSequenceDto sequence)
    {
        var response = await http.PostAsJsonAsync($"api/terminals/{terminalId}/invoice-sequences", sequence);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ActivateInvoiceSequenceAsync(int terminalId, int sequenceId)
    {
        var response = await http.PostAsync($"api/terminals/{terminalId}/invoice-sequences/{sequenceId}/activate", null);
        return response.IsSuccessStatusCode;
    }
}
