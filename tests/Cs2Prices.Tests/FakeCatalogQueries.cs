using Cs2Prices.Core.Catalog;
using Cs2Prices.Core.Domain;

namespace Cs2Prices.Tests;

internal sealed class FakeCatalogQueries : ICatalogQueries
{
    public List<MarketInfo> Markets { get; } =
    [
        new() { Id = 1, Code = "skinport", Name = "Skinport", Currency = "EUR", ItemCount = 2, LastUpdated = DateTime.UtcNow },
        new() { Id = 2, Code = "steam", Name = "Steam", Currency = "USD", ItemCount = 2, LastUpdated = DateTime.UtcNow }
    ];

    public List<CatalogRow> Rows { get; } =
    [
        new() { ItemId = 10, MarketHashName = "AK-47 | Redline (Field-Tested)", Weapon = "AK-47", SkinName = "Redline", Wear = "Field-Tested", Ask = 25m, Currency = "EUR", CapturedAt = DateTime.UtcNow },
        new() { ItemId = 11, MarketHashName = "AWP | Asiimov (Field-Tested)", Weapon = "AWP", SkinName = "Asiimov", Wear = "Field-Tested", Ask = 40m, Bid = 36m, Spread = 4m, SpreadPercent = 10m, Currency = "EUR", CapturedAt = DateTime.UtcNow }
    ];

    public List<CatalogQuery> Queries { get; } = [];
    public List<(int Item, bool Watched)> WatchCalls { get; } = [];
    public List<(int Item, byte Market, HistoryRange Range)> HistoryCalls { get; } = [];

    public ItemInfo? Item { get; set; } = new() { Id = 10, MarketHashName = "AK-47 | Redline (Field-Tested)", Weapon = "AK-47", SkinName = "Redline", Wear = "Field-Tested", IconUrl = "iconhash123" };

    public List<MarketBook> Books { get; } =
    [
        new() { MarketId = 1, MarketCode = "skinport", MarketName = "Skinport", Currency = "EUR", Ask = 25m, AskQty = 12, CapturedAt = DateTime.UtcNow },
        new()
        {
            MarketId = 2, MarketCode = "steam", MarketName = "Steam", Currency = "USD", Ask = 30m, Bid = 27m, CapturedAt = DateTime.UtcNow,
            Asks = [new BookLevel(30m, 4), new BookLevel(31m, 9)], Bids = [new BookLevel(27m, 6)]
        }
    ];

    public Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MarketInfo>>(Markets);

    public Task<PagedResult<CatalogRow>> SearchAsync(CatalogQuery query, CancellationToken ct)
    {
        Queries.Add(query);
        var rows = Rows.Where(r => string.IsNullOrWhiteSpace(query.Search) ||
                                   r.MarketHashName.Contains(query.Search, StringComparison.OrdinalIgnoreCase)).ToList();
        return Task.FromResult(new PagedResult<CatalogRow>(rows, rows.Count));
    }

    public Task<ItemInfo?> GetItemAsync(int itemId, CancellationToken ct) => Task.FromResult(Item);

    public Task<IReadOnlyList<MarketBook>> GetMarketBooksAsync(int itemId, CancellationToken ct) => Task.FromResult<IReadOnlyList<MarketBook>>(Books);

    public Task<IReadOnlyList<PricePoint>> GetHistoryAsync(int itemId, byte marketId, HistoryRange range, CancellationToken ct)
    {
        HistoryCalls.Add((itemId, marketId, range));
        var now = DateTime.UtcNow;
        return Task.FromResult<IReadOnlyList<PricePoint>>(
            [new PricePoint(now.AddHours(-2), 25m, null, null), new PricePoint(now.AddHours(-1), 26m, 24m, 25m)]);
    }

    public Task SetWatchedAsync(int itemId, bool watched, CancellationToken ct)
    {
        WatchCalls.Add((itemId, watched));
        return Task.CompletedTask;
    }
}
