using Cs2Prices.Core.Providers;

namespace Cs2Prices.Tests;

internal sealed class FakeItemRefresher : IItemRefresher
{
    public List<(string Name, bool Force)> Calls { get; } = [];

    public IReadOnlyList<MarketRefreshResult> Results { get; set; } =
        [new(1, "Steam", RefreshOutcome.Updated), new(2, "Skinport", RefreshOutcome.RateLimited, "slow down")];

    public TaskCompletionSource? Gate { get; set; }

    public async Task<IReadOnlyList<MarketRefreshResult>> RefreshAsync(string marketHashName, bool force, CancellationToken ct)
    {
        Calls.Add((marketHashName, force));
        if (Gate is not null)
            await Gate.Task;
        return Results;
    }
}
