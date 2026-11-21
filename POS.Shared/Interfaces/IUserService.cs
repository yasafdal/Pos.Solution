using POS.Shared.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace POS.Shared.Interfaces
{
    public interface IUserService
    {
        Task<List<UserDto>> GetUsersAsync();
        Task<(string Token, string Username, string Role)?> PinLoginAsync(string pinCode, int? terminalId = null, int? branchId = null);
        Task<(bool Success, string? ErrorMessage)> CreateUserAsync(CreateUserDto userDto);
        Task<(bool Success, string? ErrorMessage)> UpdateUserAsync(int userId, UpdateUserDto userDto);
        Task<bool> DeleteUserAsync(int userId);
    }
}
