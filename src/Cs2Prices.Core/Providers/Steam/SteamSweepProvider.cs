using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2Prices.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Steam lane 1: pages through market/search/render (100 items per request, sorted by name) and records the
/// lowest listing and listing count for every item. No bid side here; watchlist items are skipped because
/// the order-book lane owns them (and records a real bid and ask).
/// </summary>
public sealed class SteamSweepProvider(
    HttpClient http,
    IOptions<SteamOptions> options,
    SteamThrottle throttle,
    TimeProvider time,
    ILogger<SteamSweepProvider> logger) : IMarketProvider
{
    private const int MaxAttemptsPerPage = 3;

    private readonly SteamOptions _options = options.Value;

    public byte MarketId => MarketIds.Steam;

    public string Code => "steam-sweep";

    public TimeSpan PollInterval => _options.SweepInterval;

    public async IAsyncEnumerable<FetchResult> FetchAsync(
        IReadOnlyCollection<string> watchlist,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var skip = new HashSet<string>(watchlist, StringComparer.Ordinal);
        var batch = new List<PriceQuote>();
        var pagesInBatch = 0;
        var failed = 0;
        var rateLimitHits = 0;
        var start = 0;
        int? total = null;

        while (total is null || start < total)
        {
            var outcome = await GetPageAsync(start, cancellationToken);
            rateLimitHits += outcome.RateLimitHits;

            if (outcome.Page is null)
            {
                logger.LogWarning("Steam sweep stopped at offset {Start}: still throttled after {Attempts} attempts", start, MaxAttemptsPerPage);
                break;
            }

            total ??= outcome.Page.TotalCount;
            var results = outcome.Page.Results ?? [];
            if (results.Count == 0)
                break;

            var now = time.GetUtcNow().UtcDateTime;
            foreach (var r in results)
            {
                var name = r.HashName ?? r.Name;
                if (string.IsNullOrEmpty(name))
                {
                    failed++;
                    continue;
                }

                if (skip.Contains(name))
                    continue;

                batch.Add(Map(name, r, _options.CurrencyCode, now));
            }

            start += _options.PageSize;
            pagesInBatch++;

            if (pagesInBatch >= _options.BatchPages)
            {
                logger.LogInformation("Steam sweep progress: {Start}/{Total} items scanned", Math.Min(start, total ?? 0), total);
                yield return new FetchResult(batch.ToArray(), failed, rateLimitHits);
                batch.Clear();
                pagesInBatch = 0;
                failed = 0;
                rateLimitHits = 0;
            }
        }

        if (batch.Count > 0 || failed > 0 || rateLimitHits > 0)
            yield return new FetchResult(batch.ToArray(), failed, rateLimitHits);
    }

    private async Task<PageOutcome> GetPageAsync(int start, CancellationToken ct)
    {
        var hits = 0;
        for (var attempt = 1; attempt <= MaxAttemptsPerPage; attempt++)
        {
            await throttle.WaitAsync(ct);
            using var response = await http.GetAsync(BuildUrl(start), ct);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                hits++;
                throttle.TripCooldown(_options.RateLimitCooldown);
                logger.LogWarning("Steam returned 429 at offset {Start} (attempt {Attempt}); cooling down {Cooldown}", start, attempt, _options.RateLimitCooldown);
                continue;
            }

            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var page = await JsonSerializer.DeserializeAsync<SteamSearchPage>(stream, SerializerOptions, ct);

            if (page is { Success: true })
                return new PageOutcome(page, hits);

            // Steam sometimes answers 200 with success=false when it is throttling softly.
            hits++;
            throttle.TripCooldown(TimeSpan.FromSeconds(30));
            logger.LogWarning("Steam search returned success=false at offset {Start} (attempt {Attempt})", start, attempt);
        }

        return new PageOutcome(null, hits);
    }

    private string BuildUrl(int start)
    {
        var url = $"market/search/render/?query=&start={start}&count={_options.PageSize}" +
                  "&search_descriptions=0&sort_column=name&sort_dir=asc&appid=730&norender=1";

        foreach (var tag in _options.TypeTags)
            url += $"&category_730_Type%5B%5D={Uri.EscapeDataString(tag)}";

        return url;
    }

    internal static PriceQuote Map(string name, SteamSearchResult r, string currencyCode, DateTime capturedAtUtc) => new(
        name,
        Ask: r.SellPrice > 0 ? r.SellPrice / 100m : null,
        AskQty: r.SellListings,
        Bid: null,
        BidQty: null,
        LastSale: null,
        Currency: currencyCode,
        CapturedAtUtc: capturedAtUtc);

    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed record PageOutcome(SteamSearchPage? Page, int RateLimitHits);
}

internal sealed class SteamSearchPage
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("results")]
    public List<SteamSearchResult>? Results { get; set; }
}

internal sealed class SteamSearchResult
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("hash_name")]
    public string? HashName { get; set; }

    /// <summary>Number of active sell listings.</summary>
    [JsonPropertyName("sell_listings")]
    public int? SellListings { get; set; }

    /// <summary>Lowest listing price in minor units (cents); 0 when there are no listings.</summary>
    [JsonPropertyName("sell_price")]
    public long SellPrice { get; set; }
}
