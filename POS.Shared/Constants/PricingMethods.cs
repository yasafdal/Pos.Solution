using System;

namespace POS.Shared.Constants;

public static class PricingMethods
{
    public const string Fixed = "Fixed";
    public const string WeightBased = "WeightBased";

    public static bool IsWeightBased(string? method) =>
        string.Equals(method, WeightBased, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? method) =>
        IsWeightBased(method) ? WeightBased : Fixed;
}
