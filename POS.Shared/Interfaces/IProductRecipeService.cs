using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface IProductRecipeService
{
    Task<List<ProductRecipeDto>> GetRecipeForProductAsync(int productId);
    Task<bool> AddIngredientAsync(ProductRecipeDto recipeDto);
    Task<bool> UpdateIngredientAsync(int recipeId, ProductRecipeDto recipeDto);
    Task<bool> RemoveIngredientAsync(int recipeId);
}
