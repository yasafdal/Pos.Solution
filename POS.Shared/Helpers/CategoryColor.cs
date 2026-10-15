namespace POS.Shared.Helpers;

public static class CategoryColor
{
    public const string Default = "#2563EB";

    public static string Normalize(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
            return Default;

        for (int i = 1; i < value.Length; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return Default;
        }

        return value.ToUpperInvariant();
    }
}
