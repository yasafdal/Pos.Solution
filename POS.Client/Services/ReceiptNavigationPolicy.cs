namespace POS.Client.Services;

internal static class ReceiptNavigationPolicy
{
    // NavigateToString can report a data URL in NavigationStarting even though
    // the resulting document's origin is about:blank. Never allow external URLs.
    public static bool IsAllowed(string? uri) =>
        string.Equals(uri, "about:blank", StringComparison.OrdinalIgnoreCase)
        || (uri?.StartsWith("data:text/html;", StringComparison.OrdinalIgnoreCase) == true)
        || (uri?.StartsWith("data:text/html,", StringComparison.OrdinalIgnoreCase) == true);
}
