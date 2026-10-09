using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services
{
    public class UomService : IUomService
    {
        private readonly HttpClient _http;
        public UomService(HttpClient http)
        {
            _http = http;
        }
        public async Task<List<UomDto>> GetUomsAsync()
        {
            return await _http.GetFromJsonAsync<List<UomDto>>("api/Uoms") ?? new();
        }

        public async Task<UomDto?> GetUomByIdAsync(int id)
        {
            return await _http.GetFromJsonAsync<UomDto>($"api/Uoms/{id}");
        }

        public async Task<bool> CreateUomAsync(UomDto uom)
        {
            var response = await _http.PostAsJsonAsync("api/Uoms", uom);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UpdateUomAsync(int id, UomDto uom)
        {
            var response = await _http.PutAsJsonAsync($"api/Uoms/{id}", uom);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> DeleteUomAsync(int id)
        {
            var response = await _http.DeleteAsync($"api/Uoms/{id}");
            return response.IsSuccessStatusCode;
        }
    }
}
            
