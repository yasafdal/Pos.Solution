using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces;

public interface IProductService
{
    Task<List<ProductDto>> GetProductsAsync();
    Task<ProductDto> CreateProductAsync(ProductDto product);
    Task UpdateProductAsync(int id, ProductDto product);
    Task DeleteProductAsync(int id);
}
