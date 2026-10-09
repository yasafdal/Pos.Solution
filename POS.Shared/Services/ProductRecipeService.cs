using System;
using System.Collections.Generic;
using System.Text;
using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services
{
    public class ProductRecipeService : IProductRecipeService
    {
        private readonly HttpClient _http;

        public ProductRecipeService(HttpClient http)
        {
            _http = http;
        }

        public async Task<List<ProductRecipeDto>> GetRecipeForProductAsync(int productId)
        {
            return await _http.GetFromJsonAsync<List<ProductRecipeDto>>($"api/ProductRecipes/product/{productId}") ?? new();
        }

        public async Task<bool> AddIngredientAsync(ProductRecipeDto recipeDto)
        {
            var response = await _http.PostAsJsonAsync("api/ProductRecipes", recipeDto);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> UpdateIngredientAsync(int recipeId, ProductRecipeDto recipeDto)
        {
            var response = await _http.PutAsJsonAsync($"api/ProductRecipes/{recipeId}", recipeDto);
            return response.IsSuccessStatusCode;
        }

        public async Task<bool> RemoveIngredientAsync(int recipeId)
        {
            var response = await _http.DeleteAsync($"api/ProductRecipes/{recipeId}");
            return response.IsSuccessStatusCode;
        }
    }
}
