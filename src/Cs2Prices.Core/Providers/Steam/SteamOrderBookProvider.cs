using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cs2Prices.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Steam lane 2: for each watched item, reads the live order book from Steam's market/orderbook endpoint
/// and records the real bid (highest buy order) and ask (lowest sell order) with the quantity at the top of
/// each side plus the price ladder behind them.
/// Needs Item.IsWatched = 1 on the items to follow. At most MaxWatchlistPerCycle items are read per poll;
/// larger watchlists are covered round-robin.
/// </summary>
/// <remarks>
/// The endpoint is keyed by market hash name (the old itemordershistogram needed an item_nameid scraped from
/// the listing page, which Steam's redesigned market no longer embeds). It answers in the requester's own
/// Steam currency; the response says which one (eCurrency).
/// </remarks>
public sealed class SteamOrderBookProvider(
    HttpClient http,
    IOptions<SteamOptions> options,
    SteamThrottle throttle,
    TimeProvider time,
    ILogger<SteamOrderBookProvider> logger) : IMarketProvider
{
    private readonly SteamOptions _options = options.Value;
    private int _cursor;
    private bool _warnedEmpty;
    private bool _warnedCurrency;

    public byte MarketId => MarketIds.Steam;

    public string Code => "steam-orderbook";

    public TimeSpan PollInterval => _options.OrderBookInterval;

    public async IAsyncEnumerable<FetchResult> FetchAsync(
        IReadOnlyCollection<string> watchlist,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (watchlist.Count == 0)
        {
            if (!_warnedEmpty)
            {
                logger.LogInformation("Steam order-book lane idle: no items are flagged IsWatched = 1");
                _warnedEmpty = true;
            }

            yield break;
        }

        var names = watchlist.Order(StringComparer.Ordinal).ToList();
        var take = Math.Min(_options.MaxWatchlistPerCycle, names.Count);
        var selected = Enumerable.Range(0, take).Select(i => names[(_cursor + i) % names.Count]).ToList();
        _cursor = (_cursor + take) % names.Count;

        var quotes = new List<PriceQuote>(take);
        var failed = 0;
        var rateLimitHits = 0;

        foreach (var name in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var book = await GetOrderBookAsync(name, cancellationToken);
            if (book.RateLimited)
            {
                rateLimitHits++;
                break;
            }

            if (book.Value is null)
            {
                logger.LogWarning("Steam returned no usable order book for {Item}", name);
                failed++;
                continue;
            }

            var currency = book.Value.Currency ?? _options.CurrencyCode;
            if (!_warnedCurrency && !string.Equals(currency, _options.CurrencyCode, StringComparison.OrdinalIgnoreCase))
            {
                // The order book cannot be requested in a chosen currency, so the sweep must use the same one.
                logger.LogWarning(
                    "Steam order books arrive in {Actual} but Providers:Steam:CurrencyCode is {Configured}; " +
                    "set CurrencyId/CurrencyCode to match so both lanes store the same currency",
                    currency, _options.CurrencyCode);
                _warnedCurrency = true;
            }

            quotes.Add(new PriceQuote(
                name, book.Value.Ask, book.Value.AskQty, book.Value.Bid, book.Value.BidQty,
                LastSale: null, currency, time.GetUtcNow().UtcDateTime,
                Depth: new OrderBookDepth(book.Value.Asks, book.Value.Bids)));
        }

        logger.LogInformation("Steam order books: {Read} read, {Failed} failed, {RateLimited} rate-limited of {Selected} selected",
            quotes.Count, failed, rateLimitHits, selected.Count);

        yield return new FetchResult(quotes, failed, rateLimitHits);
    }

    private async Task<Outcome<OrderBook?>> GetOrderBookAsync(string marketHashName, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        using var response = await http.GetAsync(BuildUrl(marketHashName), ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throttle.TripCooldown(_options.RateLimitCooldown);
            logger.LogWarning("Steam returned 429 for the order book of {Item}", marketHashName);
            return new Outcome<OrderBook?>(null, true);
        }

        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound)
            return new Outcome<OrderBook?>(null, false);

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return new Outcome<OrderBook?>(ParseOrderBook(json), false);
    }

    /// <summary>market/orderbook?q=Load&amp;qp=[730,"AK-47 | Redline (Field-Tested)"]</summary>
    internal static string BuildUrl(string marketHashName) =>
        "market/orderbook?q=Load&qp=" + Uri.EscapeDataString(JsonSerializer.Serialize(new object[] { 730, marketHashName }));

    /// <summary>
    /// Parses market/orderbook. The payload is {"data":{"success":true,"data":{...}}} with prices in minor units
    /// (cents): amtMaxBuyOrder / amtMinSellOrder are the best bid and ask, and rgCompactBuyOrders /
    /// rgCompactSellOrders are flat [price, quantity, price, quantity, ...] lists, best price first, with the
    /// quantity at that price level. Returns null when Steam reports failure or the payload has no book.
    /// </summary>
    internal static OrderBook? ParseOrderBook(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var cur = doc.RootElement;

        // Unwrap {"data":{...}} envelopes, honouring an explicit success=false on the way.
        while (cur.ValueKind == JsonValueKind.Object)
        {
            if (cur.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                return null;

            if (cur.TryGetProperty("data", out var inner) && inner.ValueKind == JsonValueKind.Object)
                cur = inner;
            else
                break;
        }

        if (cur.ValueKind != JsonValueKind.Object ||
            !(cur.TryGetProperty("rgCompactBuyOrders", out _) || cur.TryGetProperty("rgCompactSellOrders", out _) ||
              cur.TryGetProperty("amtMaxBuyOrder", out _) || cur.TryGetProperty("amtMinSellOrder", out _)))
            return null;

        var asks = ReadLevels(cur, "rgCompactSellOrders");
        var bids = ReadLevels(cur, "rgCompactBuyOrders");

        return new OrderBook(
            Ask: ReadCents(cur, "amtMinSellOrder") ?? asks.FirstOrDefault()?.Price,
            AskQty: asks.FirstOrDefault()?.Quantity,
            Bid: ReadCents(cur, "amtMaxBuyOrder") ?? bids.FirstOrDefault()?.Price,
            BidQty: bids.FirstOrDefault()?.Quantity,
            Asks: asks,
            Bids: bids,
            Currency: cur.TryGetProperty("eCurrency", out var c) && c.TryGetInt32(out var id) ? CurrencyCodeFor(id) : null);
    }

    /// <summary>Steam's currency ids (ECurrencyCode) to ISO codes.</summary>
    internal static string? CurrencyCodeFor(int id) => id switch
    {
        1 => "USD", 2 => "GBP", 3 => "EUR", 4 => "CHF", 5 => "RUB", 6 => "PLN", 7 => "BRL", 8 => "JPY",
        9 => "NOK", 10 => "IDR", 11 => "MYR", 12 => "PHP", 13 => "SGD", 14 => "THB", 15 => "VND", 16 => "KRW",
        17 => "TRY", 18 => "UAH", 19 => "MXN", 20 => "CAD", 21 => "AUD", 22 => "NZD", 23 => "CNY", 24 => "INR",
        25 => "CLP", 26 => "PEN", 27 => "COP", 28 => "ZAR", 29 => "HKD", 30 => "TWD", 31 => "SAR", 32 => "AED",
        34 => "ARS", 35 => "ILS", 37 => "KZT", 38 => "KWD", 39 => "QAR", 40 => "CRC", 41 => "UYU",
        _ => null
    };

    /// <summary>Reads a flat [price, quantity, ...] list into levels (price in cents), best first.</summary>
    private static List<BookLevel> ReadLevels(JsonElement root, string property)
    {
        var levels = new List<BookLevel>();
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
            return levels;

        var items = list.EnumerateArray().ToList();
        for (var i = 0; i + 1 < items.Count && levels.Count < MaxStoredLevels; i += 2)
        {
            if (!items[i].TryGetDecimal(out var cents) || cents <= 0 || !items[i + 1].TryGetInt32(out var quantity))
                continue;

            levels.Add(new BookLevel(cents / 100m, quantity));
        }

        return levels;
    }

    private static decimal? ReadCents(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDecimal(out var cents))
            return null;

        return cents > 0 ? cents / 100m : null;
    }

    /// <summary>Levels stored per side; the UI shows fewer.</summary>
    private const int MaxStoredLevels = 15;

    internal sealed record OrderBook(
        decimal? Ask, int? AskQty, decimal? Bid, int? BidQty,
        IReadOnlyList<BookLevel> Asks, IReadOnlyList<BookLevel> Bids, string? Currency = null);

    private sealed record Outcome<T>(T? Value, bool RateLimited);
}
