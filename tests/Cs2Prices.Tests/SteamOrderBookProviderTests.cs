using System.Net;
using System.Text.Json;
using System.Web;
using Cs2Prices.Core.Providers.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Tests;

public class SteamOrderBookProviderTests
{
    // Trimmed from a real response of GET market/orderbook?q=Load&qp=[730,"AK-47 | Phantom Disruptor (Field-Tested)"]
    // (recorded 2026-10-09 from Switzerland: prices are cents, eCurrency 4 = CHF).
    private const string Book = """
        {"data":{"success":true,"data":{"amtMaxBuyOrder":525,"amtMinSellOrder":572,"eCurrency":4,
        "cBuyOrders":71894,"cSellOrders":1054,
        "rgCompactBuyOrders":[525,11,520,3,519,3,517,15],
        "rgCompactSellOrders":[572,1,587,1,592,1,605,1,606,3]}}}
        """;

    private static (SteamOrderBookProvider provider, FakeHandler handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, int maxPerCycle = 40, string currencyCode = "CHF")
    {
        var handler = new FakeHandler(respond ?? (_ => FakeHandler.Json(Book)));
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://steamcommunity.com/") };
        var options = Options.Create(new SteamOptions
        {
            MaxWatchlistPerCycle = maxPerCycle,
            CurrencyCode = currencyCode,
            MinRequestSpacing = TimeSpan.FromMilliseconds(1),
            RateLimitCooldown = TimeSpan.FromMilliseconds(10)
        });
        var provider = new SteamOrderBookProvider(
            http, options, new SteamThrottle(TimeSpan.FromMilliseconds(1), TimeProvider.System),
            TimeProvider.System, NullLogger<SteamOrderBookProvider>.Instance);
        return (provider, handler);
    }

    private const string Redline = "AK-47 | Redline (Field-Tested)";

    [Fact]
    public void Parses_bid_ask_quantities_ladders_and_currency()
    {
        var book = SteamOrderBookProvider.ParseOrderBook(Book);

        Assert.NotNull(book);
        Assert.Equal(5.72m, book.Ask);
        Assert.Equal(1, book.AskQty);
        Assert.Equal(5.25m, book.Bid);
        Assert.Equal(11, book.BidQty);
        Assert.Equal("CHF", book.Currency);
        Assert.Equal(5, book.Asks.Count);
        Assert.Equal((6.06m, 3), (book.Asks[4].Price, book.Asks[4].Quantity));
        Assert.Equal(4, book.Bids.Count);
        Assert.Equal((5.17m, 15), (book.Bids[3].Price, book.Bids[3].Quantity));
    }

    [Fact]
    public void Failure_or_unknown_payload_returns_null()
    {
        Assert.Null(SteamOrderBookProvider.ParseOrderBook("""{"data":{"success":false}}"""));
        Assert.Null(SteamOrderBookProvider.ParseOrderBook("""{"success":104}"""));
        Assert.Null(SteamOrderBookProvider.ParseOrderBook("[]"));
    }

    [Fact]
    public void Empty_book_has_no_prices()
    {
        var book = SteamOrderBookProvider.ParseOrderBook(
            """{"data":{"success":true,"data":{"amtMaxBuyOrder":0,"eCurrency":1,"rgCompactBuyOrders":[],"rgCompactSellOrders":[]}}}""");

        Assert.NotNull(book);
        Assert.Null(book.Ask);
        Assert.Null(book.Bid);
        Assert.Null(book.AskQty);
        Assert.Empty(book.Bids);
        Assert.Equal("USD", book.Currency);
    }

    [Theory]
    [InlineData(1, "USD")]
    [InlineData(3, "EUR")]
    [InlineData(4, "CHF")]
    [InlineData(999, null)]
    public void Maps_steam_currency_ids(int id, string? code) =>
        Assert.Equal(code, SteamOrderBookProvider.CurrencyCodeFor(id));

    [Fact]
    public void Url_is_keyed_by_market_hash_name()
    {
        var url = SteamOrderBookProvider.BuildUrl("AK-47 | Redline (Field-Tested)");

        Assert.StartsWith("market/orderbook?q=Load&qp=", url);
        var qp = HttpUtility.UrlDecode(url["market/orderbook?q=Load&qp=".Length..]);
        Assert.Equal("""[730,"AK-47 | Redline (Field-Tested)"]""", qp);
        Assert.Equal(730, JsonDocument.Parse(qp).RootElement[0].GetInt32());
    }

    [Fact]
    public async Task Does_nothing_without_a_watchlist()
    {
        var (provider, handler) = Create();

        var result = await provider.CollectAsync([]);

        Assert.Empty(result.Quotes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Produces_a_quote_with_both_sides_ladder_and_steams_currency()
    {
        var (provider, handler) = Create();

        var result = await provider.CollectAsync([Redline]);

        var quote = Assert.Single(result.Quotes);
        Assert.Equal(Redline, quote.MarketHashName);
        Assert.Equal(5.72m, quote.Ask);
        Assert.Equal(5.25m, quote.Bid);
        Assert.Equal(0.47m, quote.Spread);
        Assert.Equal("CHF", quote.Currency);
        Assert.Equal(5, quote.Depth!.Asks.Count);
        Assert.Single(handler.Requests);           // one request per item, no page scraping
    }

    [Fact]
    public async Task Quote_uses_the_currency_steam_answered_in()
    {
        var (provider, _) = Create(currencyCode: "USD");

        var quote = Assert.Single((await provider.CollectAsync([Redline])).Quotes);

        Assert.Equal("CHF", quote.Currency);
    }

    [Fact]
    public async Task Covers_a_large_watchlist_round_robin()
    {
        var (provider, _) = Create(maxPerCycle: 1);
        string[] watchlist = ["B | B (Factory New)", "A | A (Factory New)"];

        var first = await provider.CollectAsync(watchlist);
        var second = await provider.CollectAsync(watchlist);
        var third = await provider.CollectAsync(watchlist);

        Assert.Equal("A | A (Factory New)", Assert.Single(first.Quotes).MarketHashName);
        Assert.Equal("B | B (Factory New)", Assert.Single(second.Quotes).MarketHashName);
        Assert.Equal("A | A (Factory New)", Assert.Single(third.Quotes).MarketHashName);
    }

    [Fact]
    public async Task A_429_stops_the_cycle_and_keeps_earlier_quotes()
    {
        var calls = 0;
        var (provider, _) = Create(_ => ++calls == 1
            ? FakeHandler.Json(Book)
            : new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await provider.CollectAsync(["A | A (Factory New)", "B | B (Factory New)", "C | C (Factory New)"]);

        Assert.Single(result.Quotes);
        Assert.Equal(1, result.RateLimitHits);
    }

    [Fact]
    public async Task A_rejected_item_counts_as_a_failure_not_an_exception()
    {
        var (provider, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.BadRequest));

        var result = await provider.CollectAsync([Redline]);

        Assert.Empty(result.Quotes);
        Assert.Equal(1, result.ItemsFailed);
    }
}
