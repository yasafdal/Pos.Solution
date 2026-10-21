using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IHeldSaleService
{
    Task<List<HeldSaleDto>> GetHeldSalesAsync(int? branchId = null, int? terminalId = null);
    Task<HeldSaleDto?> GetHeldSaleByIdAsync(long id);
    Task<HeldSaleDto?> CreateHeldSaleAsync(HeldSaleCreateDto dto);
    Task<HeldSaleDto?> ResumeHeldSaleAsync(long id, int? terminalId = null);
    Task<bool> CancelHeldSaleAsync(long id);
}
