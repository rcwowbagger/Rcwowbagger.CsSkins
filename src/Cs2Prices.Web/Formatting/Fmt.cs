using System.Globalization;

namespace Cs2Prices.Web.Formatting;

/// <summary>Display formatting shared by the pages. Culture-invariant so output is the same on every machine.</summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public const string Dash = "–";

    public static string Money(decimal? value, string? currency)
    {
        if (value is not { } v)
            return Dash;

        var n = v.ToString("N2", Inv);
        return currency?.ToUpperInvariant() switch
        {
            "USD" => "$" + n,
            "EUR" => "€" + n,
            "GBP" => "£" + n,
            null or "" => n,
            var c => $"{n} {c}"
        };
    }

    public static string Qty(int? value) => value is { } v ? v.ToString("N0", Inv) : Dash;

    public static string Percent(decimal? value) => value is { } v ? v.ToString("0.0", Inv) + "%" : Dash;

    /// <summary>"3 min ago" style age of a UTC timestamp (SQL returns it with unspecified kind).</summary>
    public static string Ago(DateTime? utc, DateTime nowUtc)
    {
        if (utc is not { } t)
            return Dash;

        var age = nowUtc - t;
        if (age < TimeSpan.FromSeconds(45)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))} min ago";
        if (age < TimeSpan.FromDays(1)) return $"{(int)age.TotalHours} h ago";
        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>"AK-47 | Redline" from the parsed parts, falling back to the full market name.</summary>
    public static string ItemTitle(string? weapon, string? skin, string marketHashName) =>
        !string.IsNullOrEmpty(weapon) && !string.IsNullOrEmpty(skin) ? $"{weapon} | {skin}" : marketHashName;

    /// <summary>Short wear label for dense tables.</summary>
    public static string WearShort(string? wear) => wear switch
    {
        "Factory New" => "FN",
        "Minimal Wear" => "MW",
        "Field-Tested" => "FT",
        "Well-Worn" => "WW",
        "Battle-Scarred" => "BS",
        null or "" => Dash,
        var other => other
    };
}
