using Cs2Prices.Web.Formatting;

namespace Cs2Prices.Tests;

public class FmtTests
{
    [Theory]
    [InlineData(1234.5, "USD", "$1,234.50")]
    [InlineData(2.0, "EUR", "€2.00")]
    [InlineData(2.0, "SEK", "2.00 SEK")]
    [InlineData(null, "USD", "–")]
    public void Money(double? v, string cur, string expected) =>
        Assert.Equal(expected, Fmt.Money(v is null ? null : (decimal)v, cur));

    [Fact]
    public void Ago_buckets()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("just now", Fmt.Ago(now.AddSeconds(-10), now));
        Assert.Equal("5 min ago", Fmt.Ago(now.AddMinutes(-5), now));
        Assert.Equal("3 h ago", Fmt.Ago(now.AddHours(-3), now));
        Assert.Equal("2 d ago", Fmt.Ago(now.AddDays(-2), now));
        Assert.Equal("–", Fmt.Ago(null, now));
    }

    [Fact]
    public void Titles_and_wear()
    {
        Assert.Equal("AK-47 | Redline", Fmt.ItemTitle("AK-47", "Redline", "x"));
        Assert.Equal("x", Fmt.ItemTitle(null, null, "x"));
        Assert.Equal("FT", Fmt.WearShort("Field-Tested"));
        Assert.Equal("–", Fmt.WearShort(null));
    }

    [Fact]
    public void Image_urls_are_built_only_from_hash_shaped_values()
    {
        Assert.Equal("https://community.cloudflare.steamstatic.com/economy/image/abc-_9/360fx360f", Fmt.ImageUrl("abc-_9", "360fx360f"));
        Assert.Equal("https://community.cloudflare.steamstatic.com/economy/image/abc", Fmt.ImageUrl("abc"));
        Assert.Null(Fmt.ImageUrl(null));
        Assert.Null(Fmt.ImageUrl("a/../b"));
        Assert.Null(Fmt.ImageUrl("x\" onerror=\"y"));
    }

    [Fact]
    public void Steam_listing_url_escapes_the_name() =>
        Assert.Equal("https://steamcommunity.com/market/listings/730/AK-47%20%7C%20Redline%20%28Field-Tested%29",
            Fmt.SteamListingUrl("AK-47 | Redline (Field-Tested)"));
}
