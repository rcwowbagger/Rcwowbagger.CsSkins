namespace Cs2Prices.Core.Domain;

/// <summary>Known market ids; must match the seed in 0002_Seed_markets.sql.</summary>
public static class MarketIds
{
    public const byte Steam = 1;
    public const byte Skinport = 2;
    public const byte DMarket = 3;
    public const byte CsFloat = 4;
}

/// <summary>One price level of an order book: how many units are offered or wanted at a price.</summary>
public sealed record BookLevel(decimal Price, int Quantity);

/// <summary>
/// Price ladder behind the best bid and ask, best price first on each side.
/// Only markets that publish depth (Steam) provide it.
/// </summary>
public sealed record OrderBookDepth(IReadOnlyList<BookLevel> Asks, IReadOnlyList<BookLevel> Bids);

/// <summary>
/// Normalized price observation from one market for one item.
/// Ask = lowest sell listing, Bid = highest buy order. Either side may be unknown.
/// <see cref="IconOnly"/> quotes carry just the item picture: they register the item and set its image but
/// never touch prices (used for watched items, whose prices come from the order-book lane).
/// </summary>
public sealed record PriceQuote(
    string MarketHashName,
    decimal? Ask,
    int? AskQty,
    decimal? Bid,
    int? BidQty,
    decimal? LastSale,
    string Currency,
    DateTime CapturedAtUtc,
    OrderBookDepth? Depth = null,
    string? IconUrl = null,
    bool IconOnly = false)
{
    public decimal? Spread => Ask is { } a && Bid is { } b ? a - b : null;

    public decimal? SpreadPercent => Spread is { } s && Ask is > 0 ? s / Ask * 100m : null;
}
