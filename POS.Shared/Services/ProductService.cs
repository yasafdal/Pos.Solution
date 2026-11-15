using System.Net.Http.Json;
using POS.Shared.Models;
using POS.Shared.Interfaces;

namespace POS.Shared.Services;

public class ProductService : IProductService
{
    private readonly HttpClient _http;

    public ProductService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<ProductDto>> GetProductsAsync()
    {
        // Matches: GET /api/Products
        return await _http.GetFromJsonAsync<List<ProductDto>>("api/Products")
               ?? new List<ProductDto>();
    }

    public async Task<ProductDto> CreateProductAsync(ProductDto product)
    {
        // Matches: POST /api/Products/create-product
        var response = await _http.PostAsJsonAsync("api/Products/create-product", product);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<ProductDto>() ?? new ProductDto();
    }

    public async Task UpdateProductAsync(int id, ProductDto product)
    {
        // Matches: PUT /api/Products/update-product/{id}
        var response = await _http.PutAsJsonAsync($"api/Products/update-product/{id}", product);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteProductAsync(int id)
    {
        // Matches: DELETE /api/Products/remove-product/{id}
        var response = await _http.DeleteAsync($"api/Products/remove-product/{id}");
        response.EnsureSuccessStatusCode();
    }

    public async Task ReorderProductsAsync(int categoryId, List<int> orderedProductIds)
    {
        // Matches: PUT /api/Products/category/{categoryId}/reorder
        var response = await _http.PutAsJsonAsync($"api/Products/category/{categoryId}/reorder", orderedProductIds);
        response.EnsureSuccessStatusCode();
    }
}