using System.Text.RegularExpressions;

namespace Cs2Prices.Core.Domain;

public sealed record ParsedSkin(
    string MarketHashName,
    string Weapon,
    string SkinName,
    string Wear,
    bool IsStatTrak,
    bool IsSouvenir);

/// <summary>
/// Splits a Steam market hash name into weapon / skin / wear.
/// Only weapon skins (name contains " | " and ends with a wear in parentheses) are accepted;
/// cases, stickers, charms, graffiti, agents, vanilla knives etc. return null.
/// </summary>
public static partial class ItemNameParser
{
    private static readonly HashSet<string> Wears = new(StringComparer.Ordinal)
    {
        "Factory New", "Minimal Wear", "Field-Tested", "Well-Worn", "Battle-Scarred"
    };

    [GeneratedRegex(@"^(?<prefix>(?:★ )?(?:StatTrak™ |Souvenir )?)(?<weapon>[^|]+?) \| (?<skin>.+) \((?<wear>[^()]+)\)$")]
    private static partial Regex SkinRegex();

    public static ParsedSkin? TryParseSkin(string marketHashName)
    {
        if (string.IsNullOrWhiteSpace(marketHashName))
            return null;

        var m = SkinRegex().Match(marketHashName);
        if (!m.Success)
            return null;

        var wear = m.Groups["wear"].Value;
        if (!Wears.Contains(wear))
            return null;

        var weapon = m.Groups["weapon"].Value;
        // Exclude non-weapon categories that share the "X | Y (Z)" shape.
        if (weapon is "Sticker" or "Patch" or "Sealed Graffiti" or "Graffiti" or "Charm" or "Music Kit")
            return null;

        var prefix = m.Groups["prefix"].Value;
        return new ParsedSkin(
            marketHashName,
            weapon,
            m.Groups["skin"].Value,
            wear,
            IsStatTrak: prefix.Contains("StatTrak™", StringComparison.Ordinal),
            IsSouvenir: prefix.Contains("Souvenir", StringComparison.Ordinal));
    }
}
