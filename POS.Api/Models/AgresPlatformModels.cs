using System;
using System.Collections.Generic;

namespace POS.Api.Models;

/// <summary>
/// Client organization / tenant owning branches, terminals, and client users.
/// </summary>
public class Organization
{
    public int OrganizationId { get; set; }
    public string Code { get; set; } = null!;
    public string Name { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public virtual ICollection<Branch> Branches { get; set; } = new List<Branch>();
    public virtual ICollection<User> Users { get; set; } = new List<User>();
}

/// <summary>
/// Platform-level AGRES System Administrator account.
/// Physically separated from the client Users table; never carries a POS PIN and never syncs to POS.Client.
/// </summary>
public class AgresSystemAdmin
{
    public int SystemAdminId { get; set; }
    public string Username { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAtUtc { get; set; }
    public string? CreatedBy { get; set; }
}

/// <summary>
/// Audit trail for AGRES platform-level administrative actions (logins, licensing, orgs, system admins).
/// </summary>
public class PlatformAuditLog
{
    public long AuditLogId { get; set; }
    public string ActorUsername { get; set; } = string.Empty;
    public int? ActorSystemAdminId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
