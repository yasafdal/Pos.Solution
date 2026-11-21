using System;
using System.ComponentModel.DataAnnotations;

namespace POS.Shared.Models;

public class UpdateUserDto
{
    [Required(ErrorMessage = "Username is required.")]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [StringLength(100, MinimumLength = 6, ErrorMessage = "Password must be at least 6 characters.")]
    public string? Password { get; set; }

    [Required(ErrorMessage = "Role is required.")]
    public string Role { get; set; } = "Cashier";

    [StringLength(10, ErrorMessage = "PIN Code cannot exceed 10 digits.")]
    public string? PinCode { get; set; }

    public bool IsActive { get; set; } = true;
}

public class AgresSystemAdminDto
{
    public int SystemAdminId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
    public string? CreatedBy { get; set; }
}

public class CreateAgresSystemAdminDto
{
    [Required(ErrorMessage = "Username is required.")]
    [StringLength(50, MinimumLength = 3)]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(100, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required.")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string Password { get; set; } = string.Empty;
}

public class UpdateAgresSystemAdminDto
{
    [Required(ErrorMessage = "Display name is required.")]
    [StringLength(100, MinimumLength = 2)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters.")]
    public string? Password { get; set; }

    public bool IsActive { get; set; } = true;
}

public class OrganizationDto
{
    public int OrganizationId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
    public int BranchCount { get; set; }
    public int UserCount { get; set; }
}

public class CreateOrganizationDto
{
    [Required(ErrorMessage = "Organization code is required.")]
    [StringLength(30, MinimumLength = 2)]
    public string Code { get; set; } = string.Empty;

    [Required(ErrorMessage = "Organization name is required.")]
    [StringLength(120, MinimumLength = 2)]
    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;
}

public class PlatformAuditLogDto
{
    public long AuditLogId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public int? ActorSystemAdminId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public class ClientLicenseSummaryDto
{
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public string TerminalName { get; set; } = string.Empty;
    public string BranchName { get; set; } = string.Empty;
    public string OutletName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ValidFromUtc { get; set; }
    public DateTime? ValidUntilUtc { get; set; }
    public int DaysRemaining { get; set; }
}
