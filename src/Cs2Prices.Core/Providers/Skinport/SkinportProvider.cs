using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2Prices.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Core.Providers.Skinport;

public sealed class SkinportOptions : ProviderOptions
{
    public string BaseUrl { get; set; } = "https://api.skinport.com/";

    public string Currency { get; set; } = "EUR";
}

/// <summary>
/// Skinport bulk endpoint: GET /v1/items. One request returns every item with its lowest listing.
/// Skinport has no public bid side, so Bid stays null. The documented limit is about 8 requests / 5 minutes,
/// enforced by <see cref="RateLimitingHandler"/> on the HttpClient.
/// </summary>
public sealed class SkinportProvider(
    HttpClient http,
    IOptions<SkinportOptions> options,
    TimeProvider time,
    ILogger<SkinportProvider> logger) : IMarketProvider
{
    private readonly SkinportOptions _options = options.Value;

    public byte MarketId => MarketIds.Skinport;

    public string Code => "skinport";

    public TimeSpan PollInterval => _options.PollInterval;

    public async IAsyncEnumerable<FetchResult> FetchAsync(
        IReadOnlyCollection<string> watchlist,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var url = $"v1/items?app_id=730&currency={Uri.EscapeDataString(_options.Currency)}&tradable=0";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            logger.LogWarning("Skinport rate limit hit for {Provider}", Code);
            yield return new FetchResult([], ItemsFailed: 0, RateLimitHits: 1);
            yield break;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var quotes = new List<PriceQuote>(capacity: 20_000);
        var failed = 0;

        await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<SkinportItem>(stream, SerializerOptions, cancellationToken))
        {
            if (item is null || string.IsNullOrEmpty(item.MarketHashName))
            {
                failed++;
                continue;
            }

            quotes.Add(Map(item, now));
        }

        logger.LogInformation("Skinport returned {Count} items ({Failed} unreadable)", quotes.Count, failed);
        yield return new FetchResult(quotes, failed);
    }

    internal static PriceQuote Map(SkinportItem item, DateTime capturedAtUtc) => new(
        item.MarketHashName!,
        Ask: item.MinPrice,
        AskQty: item.Quantity,
        Bid: null,
        BidQty: null,
        LastSale: null,
        Currency: item.Currency ?? "EUR",
        CapturedAtUtc: capturedAtUtc);

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}

internal sealed class SkinportItem
{
    [JsonPropertyName("market_hash_name")]
    public string? MarketHashName { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("min_price")]
    public decimal? MinPrice { get; set; }

    [JsonPropertyName("suggested_price")]
    public decimal? SuggestedPrice { get; set; }

    [JsonPropertyName("quantity")]
    public int? Quantity { get; set; }
}
