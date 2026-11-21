using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace POS.Shared.Constants;

/// <summary>
/// Centralized identity scope, role, and permission definitions shared across
/// POS.Shared, POS.Api, POS.Client, and POS.Web.
/// Enforces strict separation between AGRES Platform System Administrators and Client-owned users.
/// </summary>
public static class PosPermissions
{
    // Claim types
    public const string IdentityScopeClaimType = "identity_scope";
    public const string OrganizationIdClaimType = "organization_id";
    public const string AgresAdminIdClaimType = "agres_admin_id";
    public const string CashierRoleClaimType = "cashier_role";

    // Identity scopes
    public const string PlatformScope = "agres_platform";
    public const string ClientScope = "client_tenant";

    // Platform role (NEVER valid in client Users table or POS.Client offline cache)
    public const string AgresSystemAdminRole = "AgresSystemAdmin";

    // Authorization policies
    public const string AgresSystemAdminPolicy = "AgresSystemAdminOnly";
    public const string ClientAdminPolicy = "ClientAdminOnly";
    public const string ClientUserManagementPolicy = "ClientUserManagement";
    public const string ClientOperationsPolicy = "ClientOperationsOnly";
    public const string TransactionHistoryPolicy = "CanViewTransactionHistory";

    // Permissions
    public const string ViewTransactionHistory = "transactions.history.view";
    public const string ApproveVoidTransaction = "transactions.void.approve";
    public const string AuthorizedTransactionHistoryRoles = "Admin,Manager,Senior Cashier,SeniorCashier";

    // Approved client-owned business roles (strict allowlist)
    public static readonly IReadOnlyList<string> ApprovedClientRoles = new[]
    {
        "Cashier",
        "Senior Cashier",
        "Manager",
        "Admin"
    };

    private static readonly IReadOnlyList<string> SupervisorPermissions = new[] { ViewTransactionHistory, ApproveVoidTransaction };
    private static readonly IReadOnlyList<string> NoPermissions = Array.Empty<string>();

    public static bool IsApprovedClientRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var clean = role.Trim();
        return clean.Equals("Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Admin", StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeClientRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return null;
        var clean = role.Trim();
        if (clean.Equals("Cashier", StringComparison.OrdinalIgnoreCase)) return "Cashier";
        if (clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
            clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase)) return "Senior Cashier";
        if (clean.Equals("Manager", StringComparison.OrdinalIgnoreCase)) return "Manager";
        if (clean.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
        return null;
    }

    public static bool IsAgresSystemAdmin(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        var scope = user.FindFirst(IdentityScopeClaimType)?.Value?.Trim();
        if (!string.Equals(scope, PlatformScope, StringComparison.Ordinal)) return false;

        return user.Claims.Any(c =>
            (c.Type == ClaimTypes.Role || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase)) &&
            string.Equals(c.Value?.Trim(), AgresSystemAdminRole, StringComparison.Ordinal));
    }

    public static bool IsClientUser(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        if (IsAgresSystemAdmin(user)) return false;

        var scope = user.FindFirst(IdentityScopeClaimType)?.Value?.Trim();
        if (string.Equals(scope, PlatformScope, StringComparison.OrdinalIgnoreCase))
            return false;

        // If identity_scope is present, it must explicitly be client_tenant.
        if (!string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, ClientScope, StringComparison.Ordinal))
            return false;

        var role = ResolveEffectiveRole(user);
        return IsApprovedClientRole(role) || string.Equals(role, "Terminal", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsClientAdmin(ClaimsPrincipal? user) =>
        IsClientUser(user) && IsAdminRole(ResolveEffectiveRole(user));

    public static bool IsClientUserManager(ClaimsPrincipal? user)
    {
        if (!IsClientUser(user)) return false;
        var role = ResolveEffectiveRole(user);
        return !string.IsNullOrWhiteSpace(role) &&
               (role.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
                role.Equals("Manager", StringComparison.OrdinalIgnoreCase));
    }

    public static int? GetOrganizationId(ClaimsPrincipal? user)
    {
        var raw = user?.FindFirst(OrganizationIdClaimType)?.Value?.Trim();
        return int.TryParse(raw, out var orgId) && orgId > 0 ? orgId : null;
    }

    public static bool IsSupervisorRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role)) return false;
        var clean = role.Trim();
        return clean.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Manager", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("Senior Cashier", StringComparison.OrdinalIgnoreCase) ||
               clean.Equals("SeniorCashier", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAdminRole(string? role) =>
        !string.IsNullOrWhiteSpace(role) &&
        role.Trim().Equals("Admin", StringComparison.OrdinalIgnoreCase);

    public static bool CanViewTransactionHistory(string? role) => IsSupervisorRole(role);

    public static bool CanApproveVoid(string? role) => IsSupervisorRole(role);

    public static bool CanApproveVoid(ClaimsPrincipal? user)
    {
        if (!IsClientUser(user)) return false;
        return CanApproveVoid(ResolveEffectiveRole(user));
    }

    public static string? ResolveEffectiveRole(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        var roles = user.Claims
            .Where(c => c.Type == ClaimTypes.Role || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        if (roles.Any(r => string.Equals(r, AgresSystemAdminRole, StringComparison.OrdinalIgnoreCase)))
        {
            return AgresSystemAdminRole;
        }

        var nonTerminalRoles = roles
            .Where(r => !string.Equals(r, "Terminal", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nonTerminalRoles.Count > 0)
        {
            return nonTerminalRoles.FirstOrDefault(IsSupervisorRole) ?? nonTerminalRoles[0];
        }

        if (roles.Any(r => string.Equals(r, "Terminal", StringComparison.OrdinalIgnoreCase)))
        {
            var cashierRole = user.FindFirst(CashierRoleClaimType)?.Value?.Trim();
            return !string.IsNullOrWhiteSpace(cashierRole) ? cashierRole : "Terminal";
        }

        return null;
    }

    public static bool IsAdmin(ClaimsPrincipal? user) => IsClientAdmin(user);

    public static bool CanViewTransactionHistory(ClaimsPrincipal? user)
    {
        if (!IsClientUser(user)) return false;
        return CanViewTransactionHistory(ResolveEffectiveRole(user));
    }

    public static bool HasPermission(string? role, string permission)
    {
        if (string.Equals(permission, ViewTransactionHistory, StringComparison.OrdinalIgnoreCase))
            return CanViewTransactionHistory(role);
        if (string.Equals(permission, ApproveVoidTransaction, StringComparison.OrdinalIgnoreCase))
            return CanApproveVoid(role);
        return false;
    }

    public static bool HasPermission(ClaimsPrincipal? user, string permission)
    {
        if (!IsClientUser(user)) return false;
        if (string.Equals(permission, ViewTransactionHistory, StringComparison.OrdinalIgnoreCase))
            return CanViewTransactionHistory(user);
        if (string.Equals(permission, ApproveVoidTransaction, StringComparison.OrdinalIgnoreCase))
            return CanApproveVoid(user);
        return false;
    }

    public static IReadOnlyList<string> GetPermissionsForRole(string? role) =>
        IsSupervisorRole(role) ? SupervisorPermissions : NoPermissions;
}
