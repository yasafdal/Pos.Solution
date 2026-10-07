using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface ICategoryService
{
    Task<List<CategoryDto>> GetCategoriesAsync();
    Task<CategoryDto> CreateCategoryAsync(CategoryDto category);
    Task UpdateCategoryAsync(int id, CategoryDto category);
    Task DeleteCategoryAsync(int id);
}