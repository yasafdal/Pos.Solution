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

        public async Task<bool> CreateTerminalAsync(TerminalDto outlet)
        {
            var response = await _http.PostAsJsonAsync("api/Terminals", outlet);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UpdateTerminalAsync(int id, TerminalDto outlet)
        {
            var response = await _http.PutAsJsonAsync($"api/Terminals/{id}", outlet);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> DeleteTerminalAsync(int id)
        {
            var response = await _http.DeleteAsync($"api/Terminals/{id}");
            return response.IsSuccessStatusCode;
        }

        public async Task<ReceiptBrandingDto?> GetReceiptBrandingAsync(int branchId)
        {
            return await _http.GetFromJsonAsync<ReceiptBrandingDto>($"api/receipt-branding/{branchId}");
        }

        public async Task<ReceiptBrandingResultDto> UploadReceiptLogoAsync(int branchId, byte[] fileBytes, string fileName, string contentType, bool applyToAllBranches = false)
        {
            using var content = new MultipartFormDataContent();
            var byteContent = new ByteArrayContent(fileBytes);
            byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            content.Add(byteContent, "file", string.IsNullOrWhiteSpace(fileName) ? "logo.png" : fileName);

            var url = $"api/receipt-branding/{branchId}/logo?applyToAllBranches={applyToAllBranches.ToString().ToLowerInvariant()}";
            var response = await _http.PostAsync(url, content);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReceiptBrandingResultDto>();
                return result ?? new ReceiptBrandingResultDto { Success = true, Message = "Receipt logo updated." };
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new ReceiptBrandingResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(errorText) ? $"Upload failed ({(int)response.StatusCode})." : errorText.Trim('"')
            };
        }

        public async Task<ReceiptBrandingResultDto> RemoveReceiptLogoAsync(int branchId, bool applyToAllBranches = false)
        {
            var url = $"api/receipt-branding/{branchId}/logo?applyToAllBranches={applyToAllBranches.ToString().ToLowerInvariant()}";
            var response = await _http.DeleteAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReceiptBrandingResultDto>();
                return result ?? new ReceiptBrandingResultDto { Success = true, Message = "Receipt logo removed." };
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new ReceiptBrandingResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(errorText) ? $"Remove failed ({(int)response.StatusCode})." : errorText.Trim('"')
            };
        }

        public async Task<ReceiptBrandingResultDto> UpdateReceiptBrandingSettingsAsync(int branchId, bool showCustomerDetailsOnReceipt, bool applyToAllBranches = false)
        {
            var payload = new UpdateReceiptBrandingSettingsDto
            {
                ShowCustomerDetailsOnReceipt = showCustomerDetailsOnReceipt,
                ApplyToAllBranches = applyToAllBranches
            };
            var response = await _http.PutAsJsonAsync($"api/receipt-branding/{branchId}/settings", payload);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ReceiptBrandingResultDto>();
                return result ?? new ReceiptBrandingResultDto { Success = true, Message = "Receipt display settings updated." };
            }

            var errorText = await response.Content.ReadAsStringAsync();
            return new ReceiptBrandingResultDto
            {
                Success = false,
                Message = string.IsNullOrWhiteSpace(errorText) ? $"Update failed ({(int)response.StatusCode})." : errorText.Trim('"')
            };
        }
    }
}