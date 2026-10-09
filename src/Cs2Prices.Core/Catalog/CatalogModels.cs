using Cs2Prices.Core.Domain;

namespace Cs2Prices.Core.Catalog;

/// <summary>A market that has collected prices, for the market selector.</summary>
public sealed class MarketInfo
{
    public byte Id { get; set; }

    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public string Currency { get; set; } = "";

    public int ItemCount { get; set; }

    /// <summary>Latest poll time seen for this market (UTC, kind unspecified as read from SQL).</summary>
    public DateTime? LastUpdated { get; set; }
}

public enum CatalogSort
{
    Name,
    Weapon,
    Wear,
    Ask,
    Bid,
    Spread,
    SpreadPercent,
    AskQty,
    BidQty,
    Updated
}

/// <summary>
/// One catalog request. <see cref="MarketId"/> selects a single market; null is reserved for the
/// consolidated all-markets view, which needs currency conversion and is not built yet.
/// </summary>
public sealed record CatalogQuery(
    byte? MarketId,
    string? Search = null,
    CatalogSort Sort = CatalogSort.Name,
    bool Descending = false,
    int Skip = 0,
    int Take = 50,
    bool WatchedOnly = false);

/// <summary>One grid row: an item as quoted by one market.</summary>
public sealed class CatalogRow
{
    public int ItemId { get; set; }

    public string MarketHashName { get; set; } = "";

    public string? Weapon { get; set; }

    public string? SkinName { get; set; }

    public string? Wear { get; set; }

    public bool IsStatTrak { get; set; }

    public bool IsSouvenir { get; set; }

    public bool IsWatched { get; set; }

    public decimal? Ask { get; set; }

    public int? AskQty { get; set; }

    public decimal? Bid { get; set; }

    public int? BidQty { get; set; }

    public decimal? LastSale { get; set; }

    public string Currency { get; set; } = "";

    public DateTime CapturedAt { get; set; }

    public decimal? Spread { get; set; }

    public decimal? SpreadPercent { get; set; }

    public int TotalCount { get; set; }
}

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount);

public sealed class ItemInfo
{
    public int Id { get; set; }

    public string MarketHashName { get; set; } = "";

    public string? Weapon { get; set; }

    public string? SkinName { get; set; }

    public string? Wear { get; set; }

    public bool IsStatTrak { get; set; }

    public bool IsSouvenir { get; set; }

    public bool IsWatched { get; set; }
}

/// <summary>
/// An item's current order book on one market: best bid and ask, plus the price ladder when the market
/// publishes depth (<see cref="HasDepth"/>).
/// </summary>
public sealed class MarketBook
{
    public byte MarketId { get; set; }

    public string MarketCode { get; set; } = "";

    public string MarketName { get; set; } = "";

    public string Currency { get; set; } = "";

    public decimal? Ask { get; set; }

    public int? AskQty { get; set; }

    public decimal? Bid { get; set; }

    public int? BidQty { get; set; }

    public decimal? LastSale { get; set; }

    public DateTime CapturedAt { get; set; }

    /// <summary>Ask levels, best (lowest) first.</summary>
    public IReadOnlyList<BookLevel> Asks { get; set; } = [];

    /// <summary>Bid levels, best (highest) first.</summary>
    public IReadOnlyList<BookLevel> Bids { get; set; } = [];

    public bool HasDepth => Asks.Count > 0 || Bids.Count > 0;

    public decimal? Spread => Ask is { } a && Bid is { } b ? a - b : null;

    public decimal? SpreadPercent => Spread is { } s && Ask is > 0 ? s / Ask * 100m : null;
}

public sealed record PricePoint(DateTime At, decimal? Ask, decimal? Bid, decimal? LastSale);

public enum HistoryRange
{
    Day,
    Week,
    Month,
    All
}
