using Cs2Prices.Core.Domain;

namespace Cs2Prices.Core.Providers;

/// <summary>One batch of normalized quotes plus problems seen while fetching it.</summary>
public sealed record FetchResult(
    IReadOnlyList<PriceQuote> Quotes,
    int ItemsFailed = 0,
    int RateLimitHits = 0);

/// <summary>
/// One implementation per market lane. Providers only fetch and normalize; persistence is separate.
/// Results are streamed in batches so a long sweep saves progress as it goes.
/// </summary>
public interface IMarketProvider
{
    byte MarketId { get; }

    /// <summary>Unique lane code, stored in CollectionRun.Provider (a market can have several lanes).</summary>
    string Code { get; }

    TimeSpan PollInterval { get; }

    /// <param name="watchlist">Market hash names flagged as watched.</param>
    IAsyncEnumerable<FetchResult> FetchAsync(IReadOnlyCollection<string> watchlist, CancellationToken cancellationToken);
}

public class ProviderOptions
{
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
}
