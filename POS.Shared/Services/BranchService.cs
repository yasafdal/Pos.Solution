using POS.Shared.Interfaces;
using POS.Shared.Models;
using System.Net.Http.Json;

namespace POS.Shared.Services
{
    public class BranchService : IBranchService
    {
        private readonly HttpClient _http;

        public BranchService(HttpClient http)
        {
            _http = http;
        }

        public async Task<List<BranchDto>> GetBranchesAsync()
        {
            return await _http.GetFromJsonAsync<List<BranchDto>>("api/Branches") ?? new();
        }

        public async Task<BranchDto?> GetBranchByIdAsync(int id)
        {
            return await _http.GetFromJsonAsync<BranchDto>($"api/Branches/{id}");
        }

        public async Task<bool> CreateBranchAsync(BranchDto branch)
        {
            var response = await _http.PostAsJsonAsync("api/Branches", branch);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UpdateBranchAsync(int id, BranchDto branch)
        {
            var response = await _http.PutAsJsonAsync($"api/Branches/{id}", branch);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> DeleteBranchAsync(int id)
        {
            var response = await _http.DeleteAsync($"api/Branches/{id}");
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> CreateOutletAsync(OutletDto outlet)
        {
            var response = await _http.PostAsJsonAsync("api/Outlets", outlet);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UpdateOutletAsync(int id, OutletDto outlet)
        {
            var response = await _http.PutAsJsonAsync($"api/Outlets/{id}", outlet);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> DeleteOutletAsync(int id)
        {
            var response = await _http.DeleteAsync($"api/Outlets/{id}");
            return response.IsSuccessStatusCode;
        }
    }
}