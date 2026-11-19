using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;

namespace POS.Shared.Constants;

/// <summary>
/// Centralized role and permission definitions shared across POS.Shared, POS.Api, POS.Client, and POS.Web.
/// </summary>
public static class PosPermissions
{
    public const string ViewTransactionHistory = "transactions.history.view";
    public const string TransactionHistoryPolicy = "CanViewTransactionHistory";
    public const string AuthorizedTransactionHistoryRoles = "Admin,Manager,Senior Cashier,SeniorCashier";
    public const string CashierRoleClaimType = "cashier_role";

    private static readonly IReadOnlyList<string> SupervisorPermissions = new[] { ViewTransactionHistory };
    private static readonly IReadOnlyList<string> NoPermissions = Array.Empty<string>();

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

    public static string? ResolveEffectiveRole(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;

        var roles = user.Claims
            .Where(c => c.Type == ClaimTypes.Role || string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value?.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

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

    public static bool IsAdmin(ClaimsPrincipal? user) => IsAdminRole(ResolveEffectiveRole(user));

    public static bool CanViewTransactionHistory(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return false;
        return CanViewTransactionHistory(ResolveEffectiveRole(user));
    }

    public static bool HasPermission(string? role, string permission)
    {
        if (string.Equals(permission, ViewTransactionHistory, StringComparison.OrdinalIgnoreCase))
            return CanViewTransactionHistory(role);
        return false;
    }

    public static bool HasPermission(ClaimsPrincipal? user, string permission)
    {
        if (string.Equals(permission, ViewTransactionHistory, StringComparison.OrdinalIgnoreCase))
            return CanViewTransactionHistory(user);
        return false;
    }

    public static IReadOnlyList<string> GetPermissionsForRole(string? role) =>
        CanViewTransactionHistory(role) ? SupervisorPermissions : NoPermissions;
}
