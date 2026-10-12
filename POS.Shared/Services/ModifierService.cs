using System.Net.Http.Json;
using POS.Shared.Interfaces;
using POS.Shared.Models;

namespace POS.Shared.Services;

public class ModifierService(HttpClient http) : IModifierService
{
    public async Task<List<ModifierGroupDto>> GetModifierGroupsAsync() =>
        await http.GetFromJsonAsync<List<ModifierGroupDto>>("api/ModifierGroups") ?? new();

    public async Task<ModifierGroupDto?> GetModifierGroupByIdAsync(int id) =>
        await http.GetFromJsonAsync<ModifierGroupDto>($"api/ModifierGroups/{id}");

    public async Task<bool> CreateModifierGroupAsync(ModifierGroupDto dto)
    {
        using var response = await http.PostAsJsonAsync("api/ModifierGroups", dto);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateModifierGroupAsync(int id, ModifierGroupDto dto)
    {
        using var response = await http.PutAsJsonAsync($"api/ModifierGroups/{id}", dto);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteModifierGroupAsync(int id)
    {
        using var response = await http.DeleteAsync($"api/ModifierGroups/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<List<ModifierOptionDto>> GetOptionsByGroupAsync(int groupId) =>
        await http.GetFromJsonAsync<List<ModifierOptionDto>>($"api/ModifierGroups/{groupId}/options") ?? new();

    public async Task<bool> CreateOptionAsync(ModifierOptionDto dto)
    {
        using var response = await http.PostAsJsonAsync("api/ModifierOptions", dto);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateOptionAsync(int id, ModifierOptionDto dto)
    {
        using var response = await http.PutAsJsonAsync($"api/ModifierOptions/{id}", dto);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteOptionAsync(int id)
    {
        using var response = await http.DeleteAsync($"api/ModifierOptions/{id}");
        return response.IsSuccessStatusCode;
    }

    public async Task<List<ProductModifierGroupDto>> GetProductModifierGroupsAsync(int productId) =>
        await http.GetFromJsonAsync<List<ProductModifierGroupDto>>($"api/Products/{productId}/modifiers") ?? new();

    public async Task<bool> AssignModifierGroupToProductAsync(int productId, int modifierGroupId, int displayOrder = 0)
    {
        using var response = await http.PostAsJsonAsync($"api/Products/{productId}/modifiers", new { modifierGroupId, displayOrder });
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveModifierGroupFromProductAsync(int productId, int modifierGroupId)
    {
        using var response = await http.DeleteAsync($"api/Products/{productId}/modifiers/{modifierGroupId}");
        return response.IsSuccessStatusCode;
    }
}
