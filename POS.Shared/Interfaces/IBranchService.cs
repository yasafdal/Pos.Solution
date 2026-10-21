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

        Task<bool> CreateTerminalAsync(TerminalDto outlet);
        Task<bool> UpdateTerminalAsync(int id, TerminalDto outlet);
        Task<bool> DeleteTerminalAsync(int id);
    }
}
