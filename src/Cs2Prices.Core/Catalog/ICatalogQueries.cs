namespace Cs2Prices.Core.Catalog;

/// <summary>Read-side queries behind the web UI.</summary>
public interface ICatalogQueries
{
    /// <summary>Markets that have collected at least one price, by market id.</summary>
    Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken);

    Task<PagedResult<CatalogRow>> SearchAsync(CatalogQuery query, CancellationToken cancellationToken);

    Task<ItemInfo?> GetItemAsync(int itemId, CancellationToken cancellationToken);

    /// <summary>The item's current order book on every market that quotes it, by market id.</summary>
    Task<IReadOnlyList<MarketBook>> GetMarketBooksAsync(int itemId, CancellationToken cancellationToken);

    /// <summary>Price snapshots for one item on one market, oldest first.</summary>
    Task<IReadOnlyList<PricePoint>> GetHistoryAsync(
        int itemId, byte marketId, HistoryRange range, CancellationToken cancellationToken);

    /// <summary>Watched items get a live order-book poll on Steam (bid, ask and depth).</summary>
    Task SetWatchedAsync(int itemId, bool watched, CancellationToken cancellationToken);
}
