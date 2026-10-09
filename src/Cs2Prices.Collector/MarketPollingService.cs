using Cs2Prices.Core.Data;
using Cs2Prices.Core.Providers;

namespace Cs2Prices.Collector;

/// <summary>
/// Runs one independent polling loop per registered provider lane so a slow or throttled market never delays another.
/// Each cycle is recorded in CollectionRun and logged with structured properties. Batches are saved as they
/// arrive, so a long sweep that is interrupted keeps what it already collected.
/// </summary>
public sealed class MarketPollingService(
    IEnumerable<IMarketProvider> providers,
    PriceRepository repository,
    TimeProvider time,
    ILogger<MarketPollingService> logger) : BackgroundService
{
    /// <summary>Unchanged prices still get a snapshot at least this often, so charts have no gaps.</summary>
    private static readonly TimeSpan Heartbeat = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var list = providers.ToList();
        if (list.Count == 0)
        {
            logger.LogWarning("No market providers are enabled; the collector has nothing to do");
            return;
        }

        logger.LogInformation("Collector started with providers: {Providers}", list.Select(p => p.Code));
        await Task.WhenAll(list.Select(p => RunProviderLoopAsync(p, stoppingToken)));
    }

    private async Task RunProviderLoopAsync(IMarketProvider provider, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(provider.PollInterval, time);

        do
        {
            await PollOnceAsync(provider, ct);
        }
        while (await WaitForNextTickAsync(timer, ct));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PollOnceAsync(IMarketProvider provider, CancellationToken ct)
    {
        var started = time.GetUtcNow().UtcDateTime;
        long runId = 0;
        int fetched = 0, snapshots = 0, failed = 0, rateLimited = 0;
        Exception? error = null;

        try
        {
            runId = await repository.StartRunAsync(provider.MarketId, provider.Code, started, ct);
            var watchlist = await repository.GetWatchlistAsync(ct);

            await foreach (var batch in provider.FetchAsync(watchlist, ct))
            {
                fetched += batch.Quotes.Count;
                failed += batch.ItemsFailed;
                rateLimited += batch.RateLimitHits;

                if (batch.Quotes.Count > 0)
                {
                    var write = await repository.WriteQuotesAsync(provider.MarketId, batch.Quotes, Heartbeat, ct);
                    snapshots += write.SnapshotsWritten;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return; // shutting down
        }
        catch (Exception ex)
        {
            error = ex;
            logger.LogError(ex, "Run {RunId} {Provider} failed", runId, provider.Code);
        }

        if (runId == 0)
            return;

        var status = error is not null ? (fetched > 0 ? "Partial" : "Failed")
            : rateLimited > 0 && fetched == 0 ? "Failed"
            : failed > 0 || rateLimited > 0 ? "Partial"
            : "Succeeded";

        var finished = time.GetUtcNow().UtcDateTime;
        try
        {
            await repository.FinishRunAsync(
                runId, finished, status, fetched, snapshots, failed, rateLimited, error?.Message, CancellationToken.None);
        }
        catch (Exception inner)
        {
            logger.LogError(inner, "Could not record result of run {RunId}", runId);
        }

        logger.LogInformation(
            "Run {RunId} {Provider} {Status}: fetched={Fetched} snapshots={Snapshots} failed={Failed} rateLimited={RateLimited} duration={DurationMs}ms",
            runId, provider.Code, status, fetched, snapshots, failed, rateLimited, (finished - started).TotalMilliseconds);
    }
}
