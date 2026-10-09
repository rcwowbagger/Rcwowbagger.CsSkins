using Cs2Prices.Core.Catalog;
using Cs2Prices.Core.Data;
using Cs2Prices.Core.Domain;

namespace Cs2Prices.Tests;

public class CatalogSqlTests
{
    [Fact]
    public void Search_splits_words_and_escapes_wildcards()
    {
        var p = CatalogSql.SearchPatterns("  ak   100%_[x] ");
        Assert.Equal(["%ak%", "%100\\%\\_\\[x]%"], p);
    }

    [Fact]
    public void Search_caps_the_number_of_words()
    {
        Assert.Equal(CatalogSql.MaxSearchTokens, CatalogSql.SearchPatterns("a b c d e f g h").Count);
        Assert.Empty(CatalogSql.SearchPatterns("   "));
    }

    [Fact]
    public void User_text_never_reaches_the_sql_text()
    {
        var (sql, parameters) = CatalogSql.BuildSearch(new CatalogQuery(2, "'; DROP TABLE Item;--"));
        Assert.DoesNotContain("DROP", sql, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(parameters);
    }

    [Fact]
    public void Page_size_is_clamped()
    {
        var (_, p) = CatalogSql.BuildSearch(new CatalogQuery(1, Take: 100000, Skip: -5));
        Assert.Equal(CatalogSql.MaxPageSize, p.Get<int>("take"));
        Assert.Equal(0, p.Get<int>("skip"));
    }

    [Fact]
    public void Consolidated_view_is_not_available_yet() =>
        Assert.Throws<NotSupportedException>(() => CatalogSql.BuildSearch(new CatalogQuery(null)));

    [Fact]
    public void Watched_filter_is_applied()
    {
        var (sql, _) = CatalogSql.BuildSearch(new CatalogQuery(1, WatchedOnly: true));
        Assert.Contains("IsWatched = 1", sql);
    }

    [Fact]
    public void Missing_prices_sort_last_in_both_directions()
    {
        foreach (var desc in new[] { false, true })
        {
            var (sql, _) = CatalogSql.BuildSearch(new CatalogQuery(1, Sort: CatalogSort.Bid, Descending: desc));
            Assert.Contains("CASE WHEN l.Bid IS NULL THEN 1 ELSE 0 END, l.Bid", sql);
        }
    }

    [Fact]
    public void History_window_starts()
    {
        var now = new DateTime(2026, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(now.AddDays(-1), CatalogSql.HistoryStart(HistoryRange.Day, now));
        Assert.Equal(now.AddDays(-7), CatalogSql.HistoryStart(HistoryRange.Week, now));
        Assert.Equal(now.AddDays(-30), CatalogSql.HistoryStart(HistoryRange.Month, now));
    }

    [Fact]
    public void Stage_tables_carry_depth_levels_and_skip_non_skins()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var depth = new OrderBookDepth(
            [new BookLevel(10m, 3), new BookLevel(11m, 5)],
            [new BookLevel(9m, 7)]);
        var quotes = new[]
        {
            new PriceQuote("AK-47 | Redline (Field-Tested)", 10m, 3, 9m, 7, null, "USD", at, depth, "iconhash"),
            new PriceQuote("Sticker | Something", 1m, 1, null, null, null, "USD", at),
            new PriceQuote("AK-47 | Redline (Field-Tested)", 99m, 1, null, null, null, "USD", at)
        };

        var (rows, levels) = PriceRepository.BuildStageTables(quotes);

        Assert.Single(rows.Rows);
        Assert.True((bool)rows.Rows[0]["HasDepth"]);
        Assert.Equal("iconhash", rows.Rows[0]["IconUrl"]);
        Assert.Equal(3, levels.Rows.Count);
        Assert.Equal("A", levels.Rows[0]["Side"]);
        Assert.Equal((short)2, levels.Rows[1]["Level"]);
        Assert.Equal("B", levels.Rows[2]["Side"]);
    }

    [Fact]
    public void Order_by_never_repeats_a_column()
    {
        foreach (var sort in Enum.GetValues<CatalogSort>())
        foreach (var desc in new[] { false, true })
        {
            var (sql, _) = CatalogSql.BuildSearch(new CatalogQuery(1, null, sort, desc));
            var orderBy = sql[sql.IndexOf("ORDER BY", StringComparison.Ordinal)..sql.IndexOf("OFFSET", StringComparison.Ordinal)];
            var terms = orderBy["ORDER BY".Length..]
                .Split(',').Select(t => t.Replace("ASC", "").Replace("DESC", "").Trim()).Where(t => t.StartsWith("i.") ).ToList();
            Assert.Equal(terms.Count, terms.Distinct().Count());
        }
    }

    [Fact]
    public void Icon_only_quotes_are_flagged_in_the_stage_table()
    {
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (rows, _) = PriceRepository.BuildStageTables(
            [new PriceQuote("AK-47 | Redline (Field-Tested)", null, null, null, null, null, "CHF", at, IconUrl: "h", IconOnly: true)]);

        Assert.True((bool)rows.Rows[0]["IconOnly"]);
        Assert.Equal("h", rows.Rows[0]["IconUrl"]);
    }
}
