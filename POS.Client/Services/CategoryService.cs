using System.Net.Http.Json;
using POS.Shared.Models;
using POS.Shared.Interfaces;

namespace POS.Client.Services
{
    public class CategoryService : ICategoryService
    {
        private readonly HttpClient _http;

        public CategoryService(HttpClient http)
        {
            _http = http;
        }

        public async Task<List<CategoryDto>> GetCategoriesAsync()
        {
            // Matches: /api/Categories
            return await _http.GetFromJsonAsync<List<CategoryDto>>("api/Categories")
                   ?? new List<CategoryDto>();
        }

        public async Task<CategoryDto> CreateCategoryAsync(CategoryDto category)
        {
            // Matches: /api/Categories/create-category
            var response = await _http.PostAsJsonAsync("api/Categories/create-category", category);

            // Read the newly created category coming back from the API
            return await response.Content.ReadFromJsonAsync<CategoryDto>() ?? new CategoryDto();
        }

        public async Task UpdateCategoryAsync(int id, CategoryDto category)
        {
            // Matches: /api/Categories/update-category/{id}
            await _http.PutAsJsonAsync($"api/Categories/update-category/{id}", category);
        }

        public async Task DeleteCategoryAsync(int id)
        {
            // Matches: /api/Categories/remove-category/{id}
            await _http.DeleteAsync($"api/Categories/remove-category/{id}");
        }
    }
}