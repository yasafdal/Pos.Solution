using System;
using System.Collections.Generic;
using System.Text;

namespace POS.Shared.Models
{
    public class UserDto
    {
        public int UserId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? PinCode { get; set; }
    }
}
