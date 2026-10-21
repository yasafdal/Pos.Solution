using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IOutletService
{
    Task<List<OutletDto>> GetOutletsAsync(int? branchId = null);
    Task<OutletDto?> GetOutletByIdAsync(int id);
    Task<bool> CreateOutletAsync(OutletDto outlet);
    Task<bool> UpdateOutletAsync(int id, OutletDto outlet);
    Task<bool> DeleteOutletAsync(int id);
}
