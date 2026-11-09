using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class TerminalService(HttpClient http) : ITerminalService
{
    public async Task<TerminalConfigDto?> ResolveTerminalAsync(string? ipAddress, string token, string? deviceUuid = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(ipAddress))
            query.Add($"ipAddress={Uri.EscapeDataString(ipAddress)}");
        if (!string.IsNullOrWhiteSpace(deviceUuid))
            query.Add($"deviceUuid={Uri.EscapeDataString(deviceUuid)}");
        var url = query.Count > 0
            ? $"api/terminals/resolve-terminal?{string.Join("&", query)}"
            : "api/terminals/resolve-terminal";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
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

    public async Task<TerminalDto?> CreateTerminalAsync(TerminalDto terminal)
    {
        var response = await http.PostAsJsonAsync("api/terminals", terminal);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TerminalDto>();
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

    public async Task<TerminalActivationResponseDto?> ActivateTerminalAsync(TerminalActivationRequestDto request)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync("api/terminals/activate", request);
        }
        catch (HttpRequestException ex)
        {
            return new TerminalActivationResponseDto
            {
                Success = false,
                Message = $"Cannot connect to backend API at {http.BaseAddress}: {ex.Message}"
            };
        }

        try
        {
            var result = await response.Content.ReadFromJsonAsync<TerminalActivationResponseDto>();
            if (result != null) return result;
        }
        catch
        {
            // Response was not JSON (e.g., 404 HTML, empty body, proxy error)
        }

        var raw = await response.Content.ReadAsStringAsync();
        var errorMsg = !string.IsNullOrWhiteSpace(raw) && raw.Length < 200 && !raw.TrimStart().StartsWith("<")
            ? raw.Trim().Trim('"')
            : $"Server returned {(int)response.StatusCode} {response.ReasonPhrase} from {http.BaseAddress}api/terminals/activate. Ensure the updated POS.Api backend is running and reachable.";

        return new TerminalActivationResponseDto
        {
            Success = false,
            Message = errorMsg
        };
    }

    public async Task<TerminalDto?> RegenerateActivationCodeAsync(int terminalId)
    {
        var response = await http.PostAsync($"api/terminals/{terminalId}/regenerate-activation-code", null);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<TerminalDto>();
    }
}
