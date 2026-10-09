using System.Collections.Concurrent;
using Cs2Prices.Core.Data;
using Cs2Prices.Core.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cs2Prices.Core.Providers;

/// <summary>What happened to one market when an item was refreshed on demand.</summary>
public sealed record MarketRefreshResult(byte MarketId, string MarketName, RefreshOutcome Outcome, string? Message = null);

public enum RefreshOutcome
{
    /// <summary>New prices were fetched and stored.</summary>
    Updated,

    /// <summary>The market does not list the item right now.</summary>
    NotListed,

    /// <summary>Refreshed a moment ago; the stored prices are current enough.</summary>
    Recent,

    RateLimited,
    Failed
}

/// <summary>Fetches one item's prices from the markets right now, instead of waiting for the collector.</summary>
public interface IItemRefresher
{
    /// <param name="force">Ignore the short per-market cool-down (used by the Refresh button).</param>
    Task<IReadOnlyList<MarketRefreshResult>> RefreshAsync(string marketHashName, bool force, CancellationToken cancellationToken);
}

/// <summary>
/// Re-uses the collector's providers for a single item. Skinport only has a whole-market endpoint, so its
/// response is filtered to the item; Steam reads the item's live order book (bid, ask and ladder).
/// A short cool-down per market keeps accidental repeat visits from tripping rate limits.
/// </summary>
public sealed class ItemRefresher(
    IServiceProvider services,
    PriceRepository repository,
    TimeProvider time,
    ILogger<ItemRefresher> logger) : IItemRefresher
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromHours(1);

    // Lane code -> display name and minimum time between automatic refreshes.
    private static readonly (string Lane, string Name, TimeSpan Cooldown)[] Lanes =
    [
        ("skinport", "Skinport", TimeSpan.FromSeconds(60)),
        ("steam-orderbook", "Steam", TimeSpan.FromSeconds(15))
    ];

    private readonly ConcurrentDictionary<(string Lane, string Item), DateTime> lastRun = new();

    public async Task<IReadOnlyList<MarketRefreshResult>> RefreshAsync(
        string marketHashName, bool force, CancellationToken cancellationToken)
    {
        var providers = services.GetServices<IMarketProvider>().ToList();
        var tasks = new List<Task<MarketRefreshResult>>();

        foreach (var (lane, name, cooldown) in Lanes)
        {
            var provider = providers.FirstOrDefault(p => p.Code == lane);
            if (provider is null)
                continue;

            tasks.Add(RefreshLaneAsync(provider, name, cooldown, marketHashName, force, cancellationToken));
        }

        return await Task.WhenAll(tasks);
    }

    private async Task<MarketRefreshResult> RefreshLaneAsync(
        IMarketProvider provider, string name, TimeSpan cooldown, string marketHashName, bool force, CancellationToken ct)
    {
        // Skinport's list is market-wide, so its cool-down is shared by all items.
        var key = (provider.Code, provider.Code == "skinport" ? "" : marketHashName);
        var now = time.GetUtcNow().UtcDateTime;

        if (!force && lastRun.TryGetValue(key, out var last) && now - last < cooldown)
            return new MarketRefreshResult(provider.MarketId, name, RefreshOutcome.Recent);

        lastRun[key] = now;

        try
        {
            var quotes = new List<PriceQuote>();
            var rateLimited = false;

            await foreach (var batch in provider.FetchAsync([marketHashName], ct))
            {
                quotes.AddRange(batch.Quotes.Where(q => q.MarketHashName == marketHashName));
                rateLimited |= batch.RateLimitHits > 0;
            }

            if (quotes.Count == 0)
                return rateLimited
                    ? new MarketRefreshResult(provider.MarketId, name, RefreshOutcome.RateLimited, "Rate limited, try again in a minute")
                    : new MarketRefreshResult(provider.MarketId, name, RefreshOutcome.NotListed, "Not listed right now");

            await repository.WriteQuotesAsync(provider.MarketId, quotes, Heartbeat, ct);
            return new MarketRefreshResult(provider.MarketId, name, RefreshOutcome.Updated);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live refresh of {Item} on {Market} failed", marketHashName, name);
            return new MarketRefreshResult(provider.MarketId, name, RefreshOutcome.Failed, ex.Message);
        }
    }
}
