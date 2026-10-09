namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Steam Community Market needs no API key, but it throttles hard (roughly 15-20 requests/minute per IP,
/// then HTTP 429 for a few minutes). Two lanes share one throttle:
/// a slow catalog sweep (ask + listing count for every skin) and a faster order-book poll
/// (real bid and ask) for the watchlist only.
/// </summary>
public sealed class SteamOptions
{
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://steamcommunity.com/";

    // Sweep lane: market/search/render, 100 items per request, ask side only.
    public bool SweepEnabled { get; set; } = true;

    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromHours(1);

    public int PageSize { get; set; } = 100;

    /// <summary>Quotes are saved after this many pages, so a long sweep keeps its progress.</summary>
    public int BatchPages { get; set; } = 10;

    /// <summary>
    /// Optional Steam type tags to narrow the sweep, e.g. "tag_CSGO_Type_Rifle".
    /// Empty = the whole market (about 35k items, of which the skins are kept).
    /// </summary>
    public List<string> TypeTags { get; set; } = [];

    // Order-book lane: itemordershistogram per watched item, gives bid and ask with depth.
    public bool OrderBookEnabled { get; set; } = true;

    public TimeSpan OrderBookInterval { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxWatchlistPerCycle { get; set; } = 40;

    /// <summary>Minimum gap between any two Steam requests (shared by both lanes).</summary>
    public TimeSpan MinRequestSpacing { get; set; } = TimeSpan.FromSeconds(4.5);

    /// <summary>Pause applied to all Steam requests after an HTTP 429.</summary>
    public TimeSpan RateLimitCooldown { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Steam currency id (1 = USD). Prices are parsed as minor units (cents).</summary>
    public int CurrencyId { get; set; } = 1;

    public string CurrencyCode { get; set; } = "USD";

    public string Country { get; set; } = "US";
}
