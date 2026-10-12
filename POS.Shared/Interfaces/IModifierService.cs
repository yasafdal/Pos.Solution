using System.Collections.Generic;
using System.Threading.Tasks;
using POS.Shared.Models;

namespace POS.Shared.Interfaces;

public interface IModifierService
{
    Task<List<ModifierGroupDto>> GetModifierGroupsAsync();
    Task<ModifierGroupDto?> GetModifierGroupByIdAsync(int id);
    Task<bool> CreateModifierGroupAsync(ModifierGroupDto dto);
    Task<bool> UpdateModifierGroupAsync(int id, ModifierGroupDto dto);
    Task<bool> DeleteModifierGroupAsync(int id);

    Task<List<ModifierOptionDto>> GetOptionsByGroupAsync(int groupId);
    Task<bool> CreateOptionAsync(ModifierOptionDto dto);
    Task<bool> UpdateOptionAsync(int id, ModifierOptionDto dto);
    Task<bool> DeleteOptionAsync(int id);

    Task<List<ProductModifierGroupDto>> GetProductModifierGroupsAsync(int productId);
    Task<bool> AssignModifierGroupToProductAsync(int productId, int modifierGroupId, int displayOrder = 0);
    Task<bool> RemoveModifierGroupFromProductAsync(int productId, int modifierGroupId);
}
