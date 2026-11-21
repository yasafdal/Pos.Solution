using System;

namespace POS.Api.Models;

public partial class User
{
    public int UserId { get; set; }

    public int OrganizationId { get; set; } = 1;

    public string Username { get; set; } = null!;

    public string PasswordHash { get; set; } = null!;

    public string Role { get; set; } = null!;

    public string? PinCode { get; set; }

    public bool IsActive { get; set; } = true;

    public virtual Organization? Organization { get; set; }
}
