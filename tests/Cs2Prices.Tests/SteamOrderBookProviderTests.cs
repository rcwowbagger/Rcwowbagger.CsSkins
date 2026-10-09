using System.Net;
using System.Web;
using Cs2Prices.Core.Providers.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Tests;

public class SteamOrderBookProviderTests
{
    // Representative itemordershistogram?norender=1 response. Prices in highest_buy_order / lowest_sell_order are cents.
    // Verify against a live response on first run: the parser only relies on the fields shown here.
    private const string Histogram = """
        {
          "success": 1,
          "sell_order_count": "1,234",
          "sell_order_price": "$21.50",
          "sell_order_table": [
            {"price": "$21.50", "price_with_fee": "$21.50", "quantity": "3"},
            {"price": "$21.60", "price_with_fee": "$21.60", "quantity": "7"}
          ],
          "buy_order_count": "2,345",
          "buy_order_price": "$19.80",
          "buy_order_table": [
            {"price": "$19.80", "quantity": "1,012"},
            {"price": "$19.70", "quantity": "5"}
          ],
          "highest_buy_order": "1980",
          "lowest_sell_order": "2150",
          "buy_order_graph": [[19.8, 1012, "x"]],
          "sell_order_graph": [[21.5, 3, "x"]],
          "price_prefix": "$",
          "price_suffix": ""
        }
        """;

    private const string ListingPage =
        "<html><script>Market_LoadOrderSpread( 175985258 ); // load the order book</script></html>";

    private sealed class MemoryNameIdStore : ISteamNameIdStore
    {
        public Dictionary<string, long> Ids { get; } = [];

        public Task<long?> GetAsync(string marketHashName, CancellationToken cancellationToken) =>
            Task.FromResult(Ids.TryGetValue(marketHashName, out var id) ? (long?)id : null);

        public Task SetAsync(string marketHashName, long nameId, CancellationToken cancellationToken)
        {
            Ids[marketHashName] = nameId;
            return Task.CompletedTask;
        }
    }

    private static (SteamOrderBookProvider provider, FakeHandler handler, MemoryNameIdStore store) Create(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null, int maxPerCycle = 40)
    {
        var handler = new FakeHandler(respond ?? DefaultResponse);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://steamcommunity.com/") };
        var store = new MemoryNameIdStore();
        var options = Options.Create(new SteamOptions
        {
            MaxWatchlistPerCycle = maxPerCycle,
            MinRequestSpacing = TimeSpan.FromMilliseconds(1),
            RateLimitCooldown = TimeSpan.FromMilliseconds(10)
        });
        var provider = new SteamOrderBookProvider(
            http, options, new SteamThrottle(TimeSpan.FromMilliseconds(1), TimeProvider.System),
            store, TimeProvider.System, NullLogger<SteamOrderBookProvider>.Instance);
        return (provider, handler, store);
    }

    private static HttpResponseMessage DefaultResponse(HttpRequestMessage request) =>
        request.RequestUri!.AbsolutePath.StartsWith("/market/listings/", StringComparison.Ordinal)
            ? FakeHandler.Html(ListingPage)
            : FakeHandler.Json(Histogram);

    private const string Redline = "AK-47 | Redline (Field-Tested)";

    [Fact]
    public void Parses_bid_and_ask_with_top_of_book_quantities()
    {
        var book = SteamOrderBookProvider.ParseOrderBook(Histogram);

        Assert.NotNull(book);
        Assert.Equal(21.50m, book.Ask);
        Assert.Equal(3, book.AskQty);
        Assert.Equal(19.80m, book.Bid);
        Assert.Equal(1012, book.BidQty);
    }

    [Fact]
    public void Failure_code_returns_null()
    {
        Assert.Null(SteamOrderBookProvider.ParseOrderBook("""{"success":104}"""));
    }

    [Fact]
    public void Empty_book_has_no_prices()
    {
        var book = SteamOrderBookProvider.ParseOrderBook(
            """{"success":1,"sell_order_table":"","buy_order_table":"","highest_buy_order":null}""");

        Assert.NotNull(book);
        Assert.Null(book.Ask);
        Assert.Null(book.Bid);
        Assert.Null(book.AskQty);
        Assert.Null(book.BidQty);
    }

    [Theory]
    [InlineData("Market_LoadOrderSpread( 175985258 );", 175985258L)]
    [InlineData("x Market_LoadOrderSpread(42) y", 42L)]
    [InlineData("nothing here", null)]
    public void Extracts_item_nameid_from_listing_page(string html, long? expected)
    {
        Assert.Equal(expected, SteamOrderBookProvider.ParseNameId(html));
    }

    [Fact]
    public async Task Does_nothing_without_a_watchlist()
    {
        var (provider, handler, _) = Create();

        var result = await provider.CollectAsync([]);

        Assert.Empty(result.Quotes);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Produces_a_quote_with_both_sides_and_spread()
    {
        var (provider, _, _) = Create();

        var result = await provider.CollectAsync([Redline]);

        var quote = Assert.Single(result.Quotes);
        Assert.Equal(Redline, quote.MarketHashName);
        Assert.Equal(21.50m, quote.Ask);
        Assert.Equal(19.80m, quote.Bid);
        Assert.Equal(1.70m, quote.Spread);
        Assert.Equal("USD", quote.Currency);
    }

    [Fact]
    public async Task Looks_up_the_nameid_once_then_reuses_it()
    {
        var (provider, handler, store) = Create();

        await provider.CollectAsync([Redline]);
        await provider.CollectAsync([Redline]);

        Assert.Equal(175985258L, store.Ids[Redline]);
        Assert.Single(handler.Requests, r => r.AbsolutePath.StartsWith("/market/listings/", StringComparison.Ordinal));
        var histograms = handler.Requests.Where(r => r.AbsolutePath == "/market/itemordershistogram").ToList();
        Assert.Equal(2, histograms.Count);
        Assert.Equal("175985258", HttpUtility.ParseQueryString(histograms[0].Query)["item_nameid"]);
    }

    [Fact]
    public async Task Covers_a_large_watchlist_round_robin()
    {
        var (provider, _, _) = Create(maxPerCycle: 1);
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
        var histogramCalls = 0;
        var (provider, _, _) = Create(request =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/market/listings/", StringComparison.Ordinal))
                return FakeHandler.Html(ListingPage);

            return ++histogramCalls == 1
                ? FakeHandler.Json(Histogram)
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        var result = await provider.CollectAsync(["A | A (Factory New)", "B | B (Factory New)", "C | C (Factory New)"]);

        Assert.Single(result.Quotes);
        Assert.Equal(1, result.RateLimitHits);
    }

    [Fact]
    public async Task Missing_nameid_counts_as_a_failure_not_an_exception()
    {
        var (provider, _, _) = Create(request =>
            request.RequestUri!.AbsolutePath.StartsWith("/market/listings/", StringComparison.Ordinal)
                ? FakeHandler.Html("<html>no id on this page</html>")
                : FakeHandler.Json(Histogram));

        var result = await provider.CollectAsync([Redline]);

        Assert.Empty(result.Quotes);
        Assert.Equal(1, result.ItemsFailed);
    }
}
