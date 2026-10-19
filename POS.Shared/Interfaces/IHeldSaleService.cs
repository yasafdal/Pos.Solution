using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IHeldSaleService
{
    Task<List<HeldSaleDto>> GetHeldSalesAsync(int? branchId = null, int? outletId = null);
    Task<HeldSaleDto?> GetHeldSaleByIdAsync(long id);
    Task<HeldSaleDto?> CreateHeldSaleAsync(HeldSaleCreateDto dto);
    Task<HeldSaleDto?> ResumeHeldSaleAsync(long id, int? outletId = null);
    Task<bool> CancelHeldSaleAsync(long id);
}
