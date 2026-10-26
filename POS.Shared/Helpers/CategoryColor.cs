namespace POS.Shared.Helpers;

public static class CategoryColor
{
    public const string Default = "#2563EB";
    public const string NeutralFallback = "#94A3B8";

    public static string Normalize(string? value, string fallback = Default)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var trimmed = value.Trim();
        if (trimmed.Length != 7 || trimmed[0] != '#')
            return fallback;

        for (int i = 1; i < trimmed.Length; i++)
        {
            if (!Uri.IsHexDigit(trimmed[i]))
                return fallback;
        }

        return trimmed.ToUpperInvariant();
    }
}
