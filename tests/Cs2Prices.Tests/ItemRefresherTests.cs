using Cs2Prices.Core.Data;
using Cs2Prices.Core.Domain;
using Cs2Prices.Core.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2Prices.Tests;

public class ItemRefresherTests
{
    private sealed class StubProvider(string code, byte market, params PriceQuote[] quotes) : IMarketProvider
    {
        public int Calls { get; private set; }
        public IReadOnlyCollection<string>? Watchlist { get; private set; }
        public byte MarketId => market;
        public string Code => code;
        public TimeSpan PollInterval => TimeSpan.FromMinutes(5);

        public async IAsyncEnumerable<FetchResult> FetchAsync(IReadOnlyCollection<string> watchlist,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++;
            Watchlist = watchlist;
            await Task.Yield();
            yield return new FetchResult(quotes);
        }
    }

    private static PriceQuote Q(string name) => new(name, 1m, 1, null, null, null, "EUR", DateTime.UtcNow);

    private static (ItemRefresher, StubProvider skinport, StubProvider steam) Create()
    {
        var skinport = new StubProvider("skinport", 2, Q("AK-47 | Redline (Field-Tested)"), Q("AWP | Asiimov (Field-Tested)"));
        var steam = new StubProvider("steam-orderbook", 1);
        var services = new ServiceCollection()
            .AddSingleton<IMarketProvider>(skinport).AddSingleton<IMarketProvider>(steam)
            .BuildServiceProvider();
        // The repository is only reached when quotes exist; these tests stop before that.
        var repo = new PriceRepository("Server=localhost;Database=none", NullLogger<PriceRepository>.Instance);
        return (new ItemRefresher(services, repo, TimeProvider.System, NullLogger<ItemRefresher>.Instance), skinport, steam);
    }

    [Fact]
    public async Task Asks_each_lane_for_just_that_item_and_reports_unlisted_items()
    {
        var (refresher, skinport, steam) = Create();

        var results = await refresher.RefreshAsync("M4A4 | Howl (Factory New)", force: false, CancellationToken.None);

        Assert.Equal(["M4A4 | Howl (Factory New)"], skinport.Watchlist);
        Assert.Equal(["M4A4 | Howl (Factory New)"], steam.Watchlist);
        Assert.All(results, r => Assert.Equal(RefreshOutcome.NotListed, r.Outcome));
        Assert.Equal(["Skinport", "Steam"], results.Select(r => r.MarketName).Order());
    }

    [Fact]
    public async Task Repeat_visits_within_the_cooldown_do_not_hit_the_markets_again()
    {
        var (refresher, skinport, steam) = Create();
        const string name = "M4A4 | Howl (Factory New)";

        await refresher.RefreshAsync(name, force: false, CancellationToken.None);
        var second = await refresher.RefreshAsync(name, force: false, CancellationToken.None);

        Assert.All(second, r => Assert.Equal(RefreshOutcome.Recent, r.Outcome));
        Assert.Equal(1, skinport.Calls);
        Assert.Equal(1, steam.Calls);

        await refresher.RefreshAsync(name, force: true, CancellationToken.None);   // Refresh button
        Assert.Equal(2, skinport.Calls);
        Assert.Equal(2, steam.Calls);
    }
}
