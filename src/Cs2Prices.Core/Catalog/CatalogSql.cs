using System.Text;
using Dapper;

namespace Cs2Prices.Core.Catalog;

/// <summary>Builds the catalog search statement. Pure string work so it can be tested without a database.</summary>
internal static class CatalogSql
{
    public const int MaxSearchTokens = 6;
    public const int MaxPageSize = 200;

    /// <summary>Sort expressions are whitelisted; user input never reaches the SQL text.</summary>
    private static string OrderBy(CatalogSort sort, bool desc)
    {
        var dir = desc ? "DESC" : "ASC";

        // Missing prices always sort last, whichever direction is chosen.
        static string NullsLast(string expr) => $"CASE WHEN {expr} IS NULL THEN 1 ELSE 0 END";

        return sort switch
        {
            CatalogSort.Weapon => $"i.Weapon {dir}, i.SkinName {dir}",
            CatalogSort.Wear => $"""
                CASE i.Wear WHEN N'Factory New' THEN 1 WHEN N'Minimal Wear' THEN 2 WHEN N'Field-Tested' THEN 3
                            WHEN N'Well-Worn' THEN 4 WHEN N'Battle-Scarred' THEN 5 ELSE 6 END {dir}
                """,
            CatalogSort.Ask => $"{NullsLast("l.Ask")}, l.Ask {dir}",
            CatalogSort.Bid => $"{NullsLast("l.Bid")}, l.Bid {dir}",
            CatalogSort.Spread => $"{NullsLast("(l.Ask - l.Bid)")}, (l.Ask - l.Bid) {dir}",
            CatalogSort.SpreadPercent =>
                $"{NullsLast("((l.Ask - l.Bid) / NULLIF(l.Ask, 0))")}, ((l.Ask - l.Bid) / NULLIF(l.Ask, 0)) {dir}",
            CatalogSort.AskQty => $"{NullsLast("l.AskQty")}, l.AskQty {dir}",
            CatalogSort.BidQty => $"{NullsLast("l.BidQty")}, l.BidQty {dir}",
            CatalogSort.Updated => $"l.CapturedAt {dir}",
            _ => $"i.MarketHashName {dir}"
        };
    }

    /// <summary>
    /// Splits a search box entry into LIKE patterns. Every word must match (AND), so "ak redline" finds
    /// "AK-47 | Redline (Field-Tested)". LIKE wildcards typed by the user are escaped.
    /// </summary>
    public static IReadOnlyList<string> SearchPatterns(string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
            return [];

        return search
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(MaxSearchTokens)
            .Select(token => "%" + EscapeLike(token) + "%")
            .ToArray();
    }

    public static string EscapeLike(string token) => token
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    public static (string Sql, DynamicParameters Parameters) BuildSearch(CatalogQuery query)
    {
        if (query.MarketId is null)
            throw new NotSupportedException(
                "The consolidated all-markets view is not built yet; it needs currency conversion. Pick a single market.");

        var parameters = new DynamicParameters();
        parameters.Add("marketId", query.MarketId.Value);
        parameters.Add("skip", Math.Max(0, query.Skip));
        parameters.Add("take", Math.Clamp(query.Take, 1, MaxPageSize));

        var sql = new StringBuilder();
        sql.AppendLine(
            """
            SELECT i.Id AS ItemId, i.MarketHashName, i.Weapon, i.SkinName, i.Wear, i.IsStatTrak, i.IsSouvenir, i.IsWatched,
                   l.Ask, l.AskQty, l.Bid, l.BidQty, l.LastSale, l.Currency, l.CapturedAt,
                   CASE WHEN l.Ask IS NOT NULL AND l.Bid IS NOT NULL THEN l.Ask - l.Bid END AS Spread,
                   CASE WHEN l.Ask > 0 AND l.Bid IS NOT NULL THEN (l.Ask - l.Bid) / l.Ask * 100 END AS SpreadPercent,
                   COUNT(*) OVER () AS TotalCount
            FROM dbo.ItemMarketLatest l
            JOIN dbo.Item i ON i.Id = l.ItemId
            WHERE l.MarketId = @marketId
            """);

        var patterns = SearchPatterns(query.Search);
        for (var n = 0; n < patterns.Count; n++)
        {
            // The second branch lets "ak47" match "AK-47".
            sql.AppendLine(
                $"  AND (i.MarketHashName LIKE @t{n} ESCAPE '\\' OR REPLACE(i.MarketHashName, N'-', N'') LIKE @t{n} ESCAPE '\\')");
            parameters.Add($"t{n}", patterns[n]);
        }

        if (query.WatchedOnly)
            sql.AppendLine("  AND i.IsWatched = 1");

        sql.AppendLine($"ORDER BY {OrderBy(query.Sort, query.Descending)}, i.Id");
        sql.AppendLine("OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;");

        return (sql.ToString(), parameters);
    }

    public const string MarketsSql = """
        SELECT m.Id, m.Code, m.Name, m.Currency, COUNT(l.ItemId) AS ItemCount, MAX(l.CapturedAt) AS LastUpdated
        FROM dbo.Market m
        JOIN dbo.ItemMarketLatest l ON l.MarketId = m.Id
        GROUP BY m.Id, m.Code, m.Name, m.Currency
        ORDER BY m.Id;
        """;

    public const string ItemSql = """
        SELECT Id, MarketHashName, Weapon, SkinName, Wear, IsStatTrak, IsSouvenir, IsWatched
        FROM dbo.Item
        WHERE Id = @itemId;
        """;

    public const string MarketBooksSql = """
        SELECT m.Id AS MarketId, m.Code AS MarketCode, m.Name AS MarketName, l.Currency,
               l.Ask, l.AskQty, l.Bid, l.BidQty, l.LastSale, l.CapturedAt
        FROM dbo.ItemMarketLatest l
        JOIN dbo.Market m ON m.Id = l.MarketId
        WHERE l.ItemId = @itemId
        ORDER BY m.Id;

        SELECT MarketId, Side, Level, Price, Quantity
        FROM dbo.OrderBookLevel
        WHERE ItemId = @itemId
        ORDER BY MarketId, Side, Level;
        """;

    public const string HistorySql = """
        SELECT TOP (@limit) CapturedAt AS [At], Ask, Bid, LastSale
        FROM dbo.PriceSnapshot
        WHERE ItemId = @itemId AND MarketId = @marketId AND CapturedAt >= @since
        ORDER BY CapturedAt DESC;
        """;

    public const string SetWatchedSql = "UPDATE dbo.Item SET IsWatched = @watched WHERE Id = @itemId;";

    /// <summary>History window start; null for the whole history.</summary>
    public static DateTime HistoryStart(HistoryRange range, DateTime nowUtc) => range switch
    {
        HistoryRange.Day => nowUtc.AddDays(-1),
        HistoryRange.Week => nowUtc.AddDays(-7),
        HistoryRange.Month => nowUtc.AddDays(-30),
        _ => new DateTime(2000, 1, 1)
    };
}
