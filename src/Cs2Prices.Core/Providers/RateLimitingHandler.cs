using System.Threading.RateLimiting;

namespace Cs2Prices.Core.Providers;

/// <summary>
/// Delays outgoing requests until the provider's rate limiter grants a permit,
/// so a market's documented limit is never exceeded regardless of retries.
/// </summary>
public sealed class RateLimitingHandler(RateLimiter limiter) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await limiter.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
        if (!lease.IsAcquired)
            throw new HttpRequestException("Local rate limiter refused the request.");

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
