using System.Net;
using System.Text;
using Cs2Prices.Core.Domain;
using Cs2Prices.Core.Providers;

namespace Cs2Prices.Tests;

internal static class ProviderTestHelpers
{
    public static async Task<List<FetchResult>> BatchesAsync(
        this IMarketProvider provider, IReadOnlyCollection<string>? watchlist = null)
    {
        var batches = new List<FetchResult>();
        await foreach (var batch in provider.FetchAsync(watchlist ?? [], CancellationToken.None))
            batches.Add(batch);
        return batches;
    }

    /// <summary>Runs the provider to completion and merges all batches into one result.</summary>
    public static async Task<FetchResult> CollectAsync(
        this IMarketProvider provider, IReadOnlyCollection<string>? watchlist = null)
    {
        var quotes = new List<PriceQuote>();
        int failed = 0, rateLimited = 0;
        foreach (var batch in await provider.BatchesAsync(watchlist))
        {
            quotes.AddRange(batch.Quotes);
            failed += batch.ItemsFailed;
            rateLimited += batch.RateLimitHits;
        }

        return new FetchResult(quotes, failed, rateLimited);
    }
}

/// <summary>HttpMessageHandler whose responses come from a delegate; records every request URI.</summary>
internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };
}
