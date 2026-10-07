using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Client.Services;

public class TaxService : ITaxService
{
    private readonly HttpClient _http;
    public TaxService(HttpClient http) => _http = http;

    public async Task<List<TaxDto>> GetTaxesAsync()
    {
        return await _http.GetFromJsonAsync<List<TaxDto>>("api/Taxes") ?? new List<TaxDto>();
    }
}