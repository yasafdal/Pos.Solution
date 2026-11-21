namespace POS.Shared.Models
{
    public class UserDto
    {
        public int UserId { get; set; }
        public int OrganizationId { get; set; } = 1;
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string? PinCode { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
