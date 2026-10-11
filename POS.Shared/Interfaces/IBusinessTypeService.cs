using POS.Shared.Models;
namespace POS.Shared.Interfaces;

public interface IBusinessTypeService
{
    Task<List<BusinessTypeDto>> GetBusinessTypesAsync();
    Task<BusinessTypeDto?> GetBusinessTypeByIdAsync(int id);
    Task<bool> CreateBusinessTypeAsync(BusinessTypeDto item);
    Task<bool> UpdateBusinessTypeAsync(int id, BusinessTypeDto item);
    Task<bool> DeleteBusinessTypeAsync(int id);
}
