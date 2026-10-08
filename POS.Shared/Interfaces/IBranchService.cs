using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces
{
    public interface IBranchService
    {
        Task<List<BranchDto>> GetBranchesAsync();
        Task<BranchDto?> GetBranchByIdAsync(int id);
        Task<bool> CreateBranchAsync(BranchDto branch);
        Task<bool> UpdateBranchAsync(int id, BranchDto branch);
        Task<bool> DeleteBranchAsync(int id);

        Task<bool> CreateOutletAsync(OutletDto outlet);
        Task<bool> UpdateOutletAsync(int id, OutletDto outlet);
        Task<bool> DeleteOutletAsync(int id);
    }
}
