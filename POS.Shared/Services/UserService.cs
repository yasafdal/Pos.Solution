using POS.Shared.Interfaces;
using POS.Shared.Models;
using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Text;

namespace POS.Shared.Services
{
    public class UserService : IUserService
    {
        private readonly HttpClient _http;

        public UserService(HttpClient http)
        {
            _http = http;
        }

        public async Task<(string Token, string Username, string Role)?> PinLoginAsync(string pinCode)
        {
            var response = await _http.PostAsJsonAsync("api/Auth/pin-login", new PinLoginDto { PinCode = pinCode });
            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<PinLoginResponse>();
            if (result == null || string.IsNullOrEmpty(result.Token))
                return null;

            return (result.Token, result.Username, result.Role);
        }

        private sealed record PinLoginResponse(string Token, string Username, string Role);

        public async Task<List<UserDto>> GetUsersAsync()
        {
            return await _http.GetFromJsonAsync<List<UserDto>>("api/Users") ?? new();
        }

        public async Task<(bool Success, string? ErrorMessage)> CreateUserAsync(CreateUserDto userDto)
        {
            var response = await _http.PostAsJsonAsync("api/Users", userDto);
            if (response.IsSuccessStatusCode)
            {
                return (true, null);
            }

            var error = await response.Content.ReadAsStringAsync();
            return (false, string.IsNullOrWhiteSpace(error) ? "Failed to create user." : error);
        }

        public async Task<bool> DeleteUserAsync(int userId)
        {
            var response = await _http.DeleteAsync($"api/Users/{userId}");
            return response.IsSuccessStatusCode;
        }
    }
}
