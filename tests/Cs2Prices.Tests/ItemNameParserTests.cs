using Cs2Prices.Core.Domain;

namespace Cs2Prices.Tests;

public class ItemNameParserTests
{
    [Fact]
    public void Parses_plain_skin()
    {
        var skin = ItemNameParser.TryParseSkin("AK-47 | Redline (Field-Tested)");

        Assert.NotNull(skin);
        Assert.Equal("AK-47", skin.Weapon);
        Assert.Equal("Redline", skin.SkinName);
        Assert.Equal("Field-Tested", skin.Wear);
        Assert.False(skin.IsStatTrak);
        Assert.False(skin.IsSouvenir);
    }

    [Fact]
    public void Parses_stattrak_skin()
    {
        var skin = ItemNameParser.TryParseSkin("StatTrak™ AWP | Asiimov (Battle-Scarred)");

        Assert.NotNull(skin);
        Assert.Equal("AWP", skin.Weapon);
        Assert.Equal("Asiimov", skin.SkinName);
        Assert.True(skin.IsStatTrak);
    }

    [Fact]
    public void Parses_souvenir_skin()
    {
        var skin = ItemNameParser.TryParseSkin("Souvenir AWP | Dragon Lore (Factory New)");

        Assert.NotNull(skin);
        Assert.True(skin.IsSouvenir);
        Assert.Equal("Dragon Lore", skin.SkinName);
    }

    [Fact]
    public void Parses_knife_skin_with_star_and_stattrak()
    {
        var skin = ItemNameParser.TryParseSkin("★ StatTrak™ Karambit | Doppler (Factory New)");

        Assert.NotNull(skin);
        Assert.Equal("Karambit", skin.Weapon);
        Assert.Equal("Doppler", skin.SkinName);
        Assert.True(skin.IsStatTrak);
    }

    [Theory]
    [InlineData("Sticker | Natus Vincere (Holo) | Katowice 2014")]
    [InlineData("Sealed Graffiti | Heart (Shark White)")]
    [InlineData("Charm | Lil' Ava (Hot)")]
    [InlineData("Patch | Phoenix")]
    [InlineData("Revolution Case")]
    [InlineData("★ Karambit")]
    [InlineData("AK-47 | Redline")]
    [InlineData("AK-47 | Redline (Mint)")]
    [InlineData("")]
    public void Rejects_items_that_are_not_weapon_skins(string name)
    {
        Assert.Null(ItemNameParser.TryParseSkin(name));
    }

    [Fact]
    public void Quote_computes_spread()
    {
        var quote = new PriceQuote("AK-47 | Redline (Field-Tested)", 10m, 5, 8m, 3, null, "USD", DateTime.UtcNow);

        Assert.Equal(2m, quote.Spread);
        Assert.Equal(20m, quote.SpreadPercent);
    }

    [Fact]
    public void Quote_has_no_spread_without_both_sides()
    {
        var quote = new PriceQuote("AK-47 | Redline (Field-Tested)", 10m, 5, null, null, null, "EUR", DateTime.UtcNow);

        Assert.Null(quote.Spread);
        Assert.Null(quote.SpreadPercent);
    }
}
