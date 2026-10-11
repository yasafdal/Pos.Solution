using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Interfaces
{
    public interface IUserService
    {
        Task<List<UserDto>> GetUsersAsync();
        Task<(string Token, string Username, string Role)?> PinLoginAsync(string pinCode);
        Task<(bool Success, string? ErrorMessage)> CreateUserAsync(CreateUserDto userDto);
        Task<bool> DeleteUserAsync(int userId);
    }
}
