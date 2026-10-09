using System.Net;
using System.Text;
using Cs2Prices.Core.Domain;
using Cs2Prices.Core.Providers.Skinport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Tests;

public class SkinportProviderTests
{
    // Shape of a real /v1/items response, trimmed. Null min_price means no active listings.
    private const string Fixture = """
        [
          {"market_hash_name":"AK-47 | Redline (Field-Tested)","currency":"EUR","suggested_price":28.5,"min_price":24.13,"max_price":40.0,"mean_price":27.0,"median_price":26.5,"quantity":142},
          {"market_hash_name":"Souvenir AWP | Dragon Lore (Factory New)","currency":"EUR","suggested_price":null,"min_price":null,"quantity":0},
          {"market_hash_name":"Revolution Case","currency":"EUR","suggested_price":0.9,"min_price":0.7,"quantity":9000}
        ]
        """;

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static (SkinportProvider provider, StubHandler handler) Create(HttpStatusCode status, string body)
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.skinport.com/") };
        var options = Options.Create(new SkinportOptions { Currency = "EUR" });
        var provider = new SkinportProvider(http, options, TimeProvider.System, NullLogger<SkinportProvider>.Instance);
        return (provider, handler);
    }

    [Fact]
    public async Task Maps_listings_to_ask_only_quotes()
    {
        var (provider, handler) = Create(HttpStatusCode.OK, Fixture);

        var result = await provider.CollectAsync();

        Assert.Equal(3, result.Quotes.Count);
        Assert.Contains("currency=EUR", handler.LastRequest!.Query);

        var redline = result.Quotes.Single(q => q.MarketHashName == "AK-47 | Redline (Field-Tested)");
        Assert.Equal(24.13m, redline.Ask);
        Assert.Equal(142, redline.AskQty);
        Assert.Null(redline.Bid);
        Assert.Equal("EUR", redline.Currency);
        Assert.Null(redline.Spread);
    }

    [Fact]
    public async Task Items_without_listings_have_no_ask()
    {
        var (provider, _) = Create(HttpStatusCode.OK, Fixture);

        var result = await provider.CollectAsync();

        var lore = result.Quotes.Single(q => q.MarketHashName.Contains("Dragon Lore"));
        Assert.Null(lore.Ask);
        Assert.Equal(0, lore.AskQty);
    }

    [Fact]
    public async Task Rate_limited_response_is_reported_not_thrown()
    {
        var (provider, _) = Create(HttpStatusCode.TooManyRequests, "{}");

        var result = await provider.CollectAsync();

        Assert.Empty(result.Quotes);
        Assert.Equal(1, result.RateLimitHits);
    }

    [Fact]
    public async Task Server_error_throws()
    {
        var (provider, _) = Create(HttpStatusCode.InternalServerError, "oops");

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CollectAsync());
    }

    [Fact]
    public void Only_skins_survive_name_filtering()
    {
        var names = new[]
        {
            "AK-47 | Redline (Field-Tested)",
            "Souvenir AWP | Dragon Lore (Factory New)",
            "Revolution Case"
        };

        Assert.Equal(2, names.Count(n => ItemNameParser.TryParseSkin(n) is not null));
    }
}
