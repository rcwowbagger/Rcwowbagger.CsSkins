using System.Net;
using System.Web;
using Cs2Prices.Core.Providers.Steam;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Tests;

public class SteamSweepProviderTests
{
    private static string Item(string name, long sellPrice, int listings) =>
        $$"""{"name":"{{name}}","hash_name":"{{name}}","sell_listings":{{listings}},"sell_price":{{sellPrice}},"sell_price_text":"x"}""";

    private static string Page(int total, params string[] items) =>
        $$"""{"success":true,"start":0,"pagesize":2,"total_count":{{total}},"results":[{{string.Join(",", items)}}]}""";

    private static (SteamSweepProvider provider, FakeHandler handler) Create(
        Func<HttpRequestMessage, HttpResponseMessage> respond, int pageSize = 2, int batchPages = 2)
    {
        var handler = new FakeHandler(respond);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://steamcommunity.com/") };
        var options = Options.Create(new SteamOptions
        {
            PageSize = pageSize,
            BatchPages = batchPages,
            MinRequestSpacing = TimeSpan.FromMilliseconds(1),
            RateLimitCooldown = TimeSpan.FromMilliseconds(10)
        });
        var provider = new SteamSweepProvider(
            http, options, new SteamThrottle(TimeSpan.FromMilliseconds(1), TimeProvider.System),
            TimeProvider.System, NullLogger<SteamSweepProvider>.Instance);
        return (provider, handler);
    }

    private static int StartOf(HttpRequestMessage request) =>
        int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["start"]!);

    private static HttpResponseMessage FivePages(HttpRequestMessage request) => StartOf(request) switch
    {
        0 => FakeHandler.Json(Page(5, Item("AK-47 | Redline (Field-Tested)", 2413, 142), Item("AWP | Asiimov (Battle-Scarred)", 3100, 8))),
        2 => FakeHandler.Json(Page(5, Item("M4A4 | Howl (Factory New)", 0, 0), Item("Revolution Case", 70, 9000))),
        _ => FakeHandler.Json(Page(5, Item("Glock-18 | Fade (Factory New)", 15000, 3)))
    };

    [Fact]
    public async Task Sweeps_every_page_and_saves_in_batches()
    {
        var (provider, handler) = Create(FivePages, batchPages: 2);

        var batches = await provider.BatchesAsync();

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, batches.Count);          // pages 1-2, then the remainder
        Assert.Equal(4, batches[0].Quotes.Count);
        Assert.Single(batches[1].Quotes);
    }

    [Fact]
    public async Task Maps_price_in_cents_and_listing_count()
    {
        var (provider, _) = Create(FivePages);

        var result = await provider.CollectAsync();

        var redline = result.Quotes.Single(q => q.MarketHashName == "AK-47 | Redline (Field-Tested)");
        Assert.Equal(24.13m, redline.Ask);
        Assert.Equal(142, redline.AskQty);
        Assert.Null(redline.Bid);
        Assert.Equal("USD", redline.Currency);
    }

    [Fact]
    public async Task Items_without_listings_have_no_ask()
    {
        var (provider, _) = Create(FivePages);

        var result = await provider.CollectAsync();

        var howl = result.Quotes.Single(q => q.MarketHashName.Contains("Howl"));
        Assert.Null(howl.Ask);
        Assert.Equal(0, howl.AskQty);
    }

    [Fact]
    public async Task Watchlist_items_are_left_to_the_order_book_lane()
    {
        var (provider, _) = Create(FivePages);

        var result = await provider.CollectAsync(["AWP | Asiimov (Battle-Scarred)"]);

        Assert.DoesNotContain(result.Quotes, q => q.MarketHashName.Contains("Asiimov"));
        Assert.Equal(4, result.Quotes.Count);
    }

    [Fact]
    public async Task Requests_ask_for_json_pages_of_the_configured_size()
    {
        var (provider, handler) = Create(FivePages);

        await provider.CollectAsync();

        var query = HttpUtility.ParseQueryString(handler.Requests[0].Query);
        Assert.Equal("730", query["appid"]);
        Assert.Equal("1", query["norender"]);
        Assert.Equal("2", query["count"]);
        Assert.Equal("name", query["sort_column"]);
    }

    [Fact]
    public async Task Recovers_from_a_429_and_reports_it()
    {
        var calls = 0;
        var (provider, _) = Create(request =>
            ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                : FivePages(request));

        var result = await provider.CollectAsync();

        Assert.Equal(1, result.RateLimitHits);
        Assert.Equal(5, result.Quotes.Count);
    }

    [Fact]
    public async Task Gives_up_after_repeated_429_and_keeps_what_it_has()
    {
        var (provider, handler) = Create(request =>
            StartOf(request) == 0
                ? FivePages(request)
                : new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            batchPages: 10);

        var result = await provider.CollectAsync();

        Assert.Equal(2, result.Quotes.Count);            // first page survived
        Assert.Equal(3, result.RateLimitHits);           // three attempts on the second page
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task Stops_when_steam_returns_no_more_results()
    {
        var (provider, handler) = Create(request =>
            StartOf(request) == 0
                ? FakeHandler.Json(Page(100, Item("AK-47 | Redline (Field-Tested)", 2413, 1), Item("AWP | Asiimov (Battle-Scarred)", 3100, 1)))
                : FakeHandler.Json(Page(100)));

        var result = await provider.CollectAsync();

        Assert.Equal(2, result.Quotes.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Server_error_throws_after_earlier_batches_were_delivered()
    {
        var (provider, _) = Create(
            request => StartOf(request) == 0
                ? FivePages(request)
                : new HttpResponseMessage(HttpStatusCode.InternalServerError),
            batchPages: 1);

        var delivered = new List<int>();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var batch in provider.FetchAsync([], CancellationToken.None))
                delivered.Add(batch.Quotes.Count);
        });

        Assert.Equal([2], delivered);
    }
}
