namespace POS.Shared.Helpers;

/// <summary>Resolves a stored product ImageUrl to a browser-loadable URL. Stored values are never modified.</summary>
public static class ProductImageUrl
{
    // Neutral grey "image" glyph used when an image fails to load.
    public const string PlaceholderDataUri =
        "data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='60' height='60'><rect width='60' height='60' fill='%23f8f9fa'/><path d='M14 44l10-12 8 9 6-7 8 10z' fill='%23adb5bd'/><circle cx='22' cy='22' r='5' fill='%23adb5bd'/></svg>";

    /// <summary>Inline handler: swap a broken image for the placeholder once (no onerror loop).</summary>
    public const string OnErrorHandler = "this.onerror=null;this.src=\"" + PlaceholderDataUri + "\";";

    public static string Resolve(string? imageUrl, string? apiBaseUrl)
    {
        var value = imageUrl?.Trim();
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var hasBase = Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var baseUri)
            && (baseUri.Scheme == Uri.UriSchemeHttp || baseUri.Scheme == Uri.UriSchemeHttps);

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && !value.StartsWith('/'))
        {
            // Absolute URLs are kept, except a stored loopback host (dev leftovers), which no browser can reach in UAT.
            if (hasBase && absolute.IsLoopback && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                return Combine(baseUri!, absolute.PathAndQuery);
            return value;
        }

        if (value.StartsWith("//")) return value; // protocol-relative: already points at a host
        return hasBase ? Combine(baseUri!, value) : "/" + value.TrimStart('/');
    }

    private static string Combine(Uri baseUri, string path) =>
        baseUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/" + path.TrimStart('/');
}
