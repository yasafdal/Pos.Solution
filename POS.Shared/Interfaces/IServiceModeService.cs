using POS.Shared.Models;
namespace POS.Shared.Interfaces;

public interface IServiceModeService
{
    Task<List<ServiceModeDto>> GetServiceModesAsync();
    Task<ServiceModeDto?> GetServiceModeByIdAsync(int id);
    Task<bool> CreateServiceModeAsync(ServiceModeDto item);
    Task<bool> UpdateServiceModeAsync(int id, ServiceModeDto item);
    Task<bool> DeleteServiceModeAsync(int id);
}
