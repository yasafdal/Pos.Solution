using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace POS.Shared.Services;

public static class ProductShortCodeGenerator
{
    private static readonly Dictionary<string, string> CommonAbbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        { "ICED", "ICE" },
        { "CAFE", "" },
        { "HOT", "HOT" },
        { "CAPPUCCINO", "CAP" },
        { "BLUEBERRY", "BLUE" },
        { "MUFFIN", "MUFF" },
        { "CHEESE", "CHZ" },
        { "SANDWICH", "SW" },
        { "SYRUP", "SYR" },
        { "PUMP", "" },
        { "MILK TEA", "MT" },
        { "EXTRA", "XTRA" },
        { "CHOCOLATE", "CHOC" },
        { "STRAWBERRY", "STRW" },
        { "CARAMEL", "CARM" },
        { "ESPRESSO", "ESP" },
        { "AMERICANO", "AMER" },
        { "MACCHIATO", "MAC" },
        { "VANILLA", "VAN" },
        { "HAZELNUT", "HAZ" },
        { "MATCHA", "MATCHA" },
        { "BREAKFAST", "BKFST" },
        { "CHICKEN", "CHK" },
        { "BURGER", "BGR" },
        { "FRENCH FRIES", "FRIES" },
        { "MEAL", "" },
        { "SINKERS", "" }
    };

    public static string GenerateShortCode(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return string.Empty;
        }

        string cleaned = productName.Trim();

        // Check multi-word phrase dictionary replacements first
        foreach (var kvp in CommonAbbreviations.Where(k => k.Key.Contains(' ')))
        {
            if (cleaned.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = Regex.Replace(cleaned, Regex.Escape(kvp.Key), kvp.Value, RegexOptions.IgnoreCase);
            }
        }

        // Split into words, apply single word abbreviations
        var words = Regex.Split(cleaned, @"\s+")
            .Where(w => !string.IsNullOrEmpty(w))
            .ToList();

        var processedWords = new List<string>();
        foreach (var word in words)
        {
            // Remove punctuation from individual words except alphanumeric and ampersand
            var cleanWord = Regex.Replace(word, @"[^\w&]", string.Empty).ToUpperInvariant();
            if (string.IsNullOrEmpty(cleanWord)) continue;

            if (CommonAbbreviations.TryGetValue(cleanWord, out var abbr))
            {
                if (!string.IsNullOrEmpty(abbr))
                {
                    processedWords.Add(abbr);
                }
            }
            else
            {
                // If word is very long (> 7 chars) and not an abbreviation, truncate or take first 4-5 chars if appropriate,
                // or keep as is if reasonable. Let's keep words as uppercase.
                processedWords.Add(cleanWord);
            }
        }

        var result = string.Join(" ", processedWords);
        result = Regex.Replace(result, @"\s+", " ").Trim();

        // If result exceeds 30 characters, truncate cleanly at word boundary or 30 chars max
        if (result.Length > 30)
        {
            result = result.Substring(0, 30).TrimEnd();
        }

        return result;
    }
}
