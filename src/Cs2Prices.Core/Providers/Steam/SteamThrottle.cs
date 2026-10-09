using System.Threading.RateLimiting;

namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Single gate for every request to steamcommunity.com: enforces a minimum spacing between requests
/// and a shared cooldown that any caller can trigger after an HTTP 429.
/// </summary>
public sealed class SteamThrottle : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter;
    private readonly TimeProvider _time;
    private long _cooldownUntilTicks;

    public SteamThrottle(TimeSpan minSpacing, TimeProvider time)
    {
        _time = time;
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = minSpacing <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : minSpacing,
            AutoReplenishment = true,
            QueueLimit = 1000,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
    }

    public DateTime CooldownUntilUtc => new(Interlocked.Read(ref _cooldownUntilTicks), DateTimeKind.Utc);

    /// <summary>Waits out any cooldown, then waits for the next request slot.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var remaining = CooldownUntilUtc - _time.GetUtcNow().UtcDateTime;
            if (remaining <= TimeSpan.Zero)
                break;
            await Task.Delay(remaining, _time, cancellationToken).ConfigureAwait(false);
        }

        using var lease = await _limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new InvalidOperationException("Steam throttle queue is full.");
    }

    /// <summary>Blocks all Steam requests until <paramref name="duration"/> from now (never shortens an existing cooldown).</summary>
    public void TripCooldown(TimeSpan duration)
    {
        var until = (_time.GetUtcNow().UtcDateTime + duration).Ticks;
        long current;
        do
        {
            current = Interlocked.Read(ref _cooldownUntilTicks);
            if (until <= current)
                return;
        }
        while (Interlocked.CompareExchange(ref _cooldownUntilTicks, until, current) != current);
    }

    public void Dispose() => _limiter.Dispose();
}
