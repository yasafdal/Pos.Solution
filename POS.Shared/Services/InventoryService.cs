using POS.Shared.Interfaces;
using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;

namespace POS.Shared.Services
{
    public class InventoryService : IInventoryService
    {
        private readonly HttpClient _http;

        public InventoryService(HttpClient http)
        {
            _http = http;
        }

        public async Task<List<BranchInventoryDto>> GetBranchInventoryAsync(int branchId)
        {
            return await _http.GetFromJsonAsync<List<BranchInventoryDto>>($"api/Inventory/branch/{branchId}") ?? new();
        }

        public async Task<bool> AdjustStockAsync(StockAdjustmentDto dto)
        {
            var response = await _http.PostAsJsonAsync("api/Inventory/adjust", dto);
            return response.IsSuccessStatusCode;
        }
    }
}
