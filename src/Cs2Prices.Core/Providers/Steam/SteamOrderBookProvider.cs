using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cs2Prices.Core.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Steam lane 2: for each watched item, reads the live order book (itemordershistogram) and records the real
/// bid (highest buy order) and ask (lowest sell order) with the quantity at the top of each side.
/// Needs Item.IsWatched = 1 on the items to follow. At most MaxWatchlistPerCycle items are read per poll;
/// larger watchlists are covered round-robin.
/// </summary>
public sealed partial class SteamOrderBookProvider(
    HttpClient http,
    IOptions<SteamOptions> options,
    SteamThrottle throttle,
    ISteamNameIdStore nameIds,
    TimeProvider time,
    ILogger<SteamOrderBookProvider> logger) : IMarketProvider
{
    private readonly SteamOptions _options = options.Value;
    private int _cursor;
    private bool _warnedEmpty;

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

            var nameId = await nameIds.GetAsync(name, cancellationToken);
            if (nameId is null)
            {
                var lookup = await LookupNameIdAsync(name, cancellationToken);
                if (lookup.RateLimited)
                {
                    rateLimitHits++;
                    break;
                }

                if (lookup.Value is null)
                {
                    logger.LogWarning("Could not find Steam item_nameid for {Item}", name);
                    failed++;
                    continue;
                }

                nameId = lookup.Value;
                await nameIds.SetAsync(name, nameId.Value, cancellationToken);
            }

            var book = await GetOrderBookAsync(nameId.Value, cancellationToken);
            if (book.RateLimited)
            {
                rateLimitHits++;
                break;
            }

            if (book.Value is null)
            {
                failed++;
                continue;
            }

            quotes.Add(new PriceQuote(
                name, book.Value.Ask, book.Value.AskQty, book.Value.Bid, book.Value.BidQty,
                LastSale: null, _options.CurrencyCode, time.GetUtcNow().UtcDateTime,
                Depth: new OrderBookDepth(book.Value.Asks, book.Value.Bids)));
        }

        logger.LogInformation("Steam order books: {Read} read, {Failed} failed, {RateLimited} rate-limited of {Selected} selected",
            quotes.Count, failed, rateLimitHits, selected.Count);

        yield return new FetchResult(quotes, failed, rateLimitHits);
    }

    private async Task<Outcome<long?>> LookupNameIdAsync(string name, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        using var response = await http.GetAsync($"market/listings/730/{Uri.EscapeDataString(name)}", ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throttle.TripCooldown(_options.RateLimitCooldown);
            logger.LogWarning("Steam returned 429 while looking up item_nameid for {Item}", name);
            return new Outcome<long?>(null, true);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
            return new Outcome<long?>(null, false);

        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        return new Outcome<long?>(ParseNameId(html), false);
    }

    private async Task<Outcome<OrderBook?>> GetOrderBookAsync(long nameId, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        var url = $"market/itemordershistogram?country={_options.Country}&language=english" +
                  $"&currency={_options.CurrencyId}&item_nameid={nameId}&two_factor=0&norender=1";
        using var response = await http.GetAsync(url, ct);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throttle.TripCooldown(_options.RateLimitCooldown);
            logger.LogWarning("Steam returned 429 for order book of item_nameid {NameId}", nameId);
            return new Outcome<OrderBook?>(null, true);
        }

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        return new Outcome<OrderBook?>(ParseOrderBook(json), false);
    }

    /// <summary>The listing page embeds the id as Market_LoadOrderSpread( 175985258 ).</summary>
    internal static long? ParseNameId(string html)
    {
        var m = NameIdRegex().Match(html);
        return m.Success && long.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    /// <summary>
    /// Parses itemordershistogram?norender=1. highest_buy_order / lowest_sell_order are minor units (cents);
    /// the *_order_table arrays list price levels best-first with a quantity each.
    /// Returns null when Steam reports failure (e.g. success = 104 for an unknown item_nameid).
    /// </summary>
    internal static OrderBook? ParseOrderBook(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !IsSuccess(root))
            return null;

        var asks = ReadLevels(root, "sell_order_table");
        var bids = ReadLevels(root, "buy_order_table");

        return new OrderBook(
            Ask: ReadCents(root, "lowest_sell_order") ?? asks.FirstOrDefault()?.Price,
            AskQty: ReadFirstQuantity(root, "sell_order_table"),
            Bid: ReadCents(root, "highest_buy_order") ?? bids.FirstOrDefault()?.Price,
            BidQty: ReadFirstQuantity(root, "buy_order_table"),
            Asks: asks,
            Bids: bids);
    }

    /// <summary>
    /// Reads the price ladder from a *_order_table array, best price first. Prices there are display strings
    /// such as "$1,234.56"; rows that cannot be read are skipped.
    /// </summary>
    private static List<BookLevel> ReadLevels(JsonElement root, string property)
    {
        var levels = new List<BookLevel>();
        if (!root.TryGetProperty(property, out var table) || table.ValueKind != JsonValueKind.Array)
            return levels;

        foreach (var row in table.EnumerateArray())
        {
            if (levels.Count >= MaxStoredLevels)
                break;

            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("price", out var priceElement) || !TryReadPrice(priceElement, out var price) ||
                !row.TryGetProperty("quantity", out var qtyElement) || !TryReadInt(qtyElement, out var quantity))
                continue;

            levels.Add(new BookLevel(price, quantity));
        }

        return levels;
    }

    private static bool TryReadPrice(JsonElement element, out decimal price)
    {
        price = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return element.TryGetDecimal(out price) && price > 0;
            case JsonValueKind.String:
                var m = PriceTextRegex().Match(element.GetString() ?? "");
                return m.Success &&
                       decimal.TryParse(m.Value.Replace(",", ""), System.Globalization.NumberStyles.Number,
                           System.Globalization.CultureInfo.InvariantCulture, out price) &&
                       price > 0;
            default:
                return false;
        }
    }

    private static bool TryReadInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(
                element.GetString()?.Replace(",", ""), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out value),
            _ => false
        };
    }

    private static bool IsSuccess(JsonElement root)
    {
        if (!root.TryGetProperty("success", out var s))
            return false;

        return s.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => s.TryGetInt32(out var n) && n == 1,
            JsonValueKind.String => s.GetString() == "1",
            _ => false
        };
    }

    private static decimal? ReadCents(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var p))
            return null;

        decimal cents;
        switch (p.ValueKind)
        {
            case JsonValueKind.Number when p.TryGetDecimal(out var n):
                cents = n;
                break;
            case JsonValueKind.String when decimal.TryParse(
                p.GetString()?.Replace(",", ""), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var s):
                cents = s;
                break;
            default:
                return null;
        }

        return cents > 0 ? cents / 100m : null;
    }

    private static int? ReadFirstQuantity(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var table) ||
            table.ValueKind != JsonValueKind.Array ||
            table.GetArrayLength() == 0)
            return null;

        var first = table[0];
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("quantity", out var q))
            return null;

        return q.ValueKind switch
        {
            JsonValueKind.Number when q.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(
                q.GetString()?.Replace(",", ""), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var s) => s,
            _ => null
        };
    }

    [GeneratedRegex(@"Market_LoadOrderSpread\(\s*(\d+)\s*\)")]
    private static partial Regex NameIdRegex();

    /// <summary>First number in a display price such as "$1,234.56".</summary>
    [GeneratedRegex(@"\d[\d,]*(?:\.\d+)?")]
    private static partial Regex PriceTextRegex();

    /// <summary>Levels stored per side; the UI shows fewer.</summary>
    private const int MaxStoredLevels = 15;

    internal sealed record OrderBook(
        decimal? Ask, int? AskQty, decimal? Bid, int? BidQty,
        IReadOnlyList<BookLevel> Asks, IReadOnlyList<BookLevel> Bids);

    private sealed record Outcome<T>(T? Value, bool RateLimited);
}
