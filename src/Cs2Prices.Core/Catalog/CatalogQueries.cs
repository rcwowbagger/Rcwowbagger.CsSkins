using Cs2Prices.Core.Domain;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Cs2Prices.Core.Catalog;

public sealed class CatalogQueries(string connectionString, TimeProvider time) : ICatalogQueries
{
    private const int HistoryLimit = 5000;

    private SqlConnection CreateConnection() => new(connectionString);

    public async Task<IReadOnlyList<MarketInfo>> GetMarketsAsync(CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        var markets = await conn.QueryAsync<MarketInfo>(
            new CommandDefinition(CatalogSql.MarketsSql, cancellationToken: cancellationToken));
        return markets.AsList();
    }

    public async Task<PagedResult<CatalogRow>> SearchAsync(CatalogQuery query, CancellationToken cancellationToken)
    {
        var (sql, parameters) = CatalogSql.BuildSearch(query);

        await using var conn = CreateConnection();
        var rows = (await conn.QueryAsync<CatalogRow>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))).AsList();

        if (rows.Count > 0)
            return new PagedResult<CatalogRow>(rows, rows[0].TotalCount);

        // A page past the end returns no rows, so the window count is lost; ask again for the first row.
        if (query.Skip > 0)
        {
            var (firstSql, firstParams) = CatalogSql.BuildSearch(query with { Skip = 0, Take = 1 });
            var first = (await conn.QueryAsync<CatalogRow>(
                new CommandDefinition(firstSql, firstParams, cancellationToken: cancellationToken))).AsList();
            return new PagedResult<CatalogRow>([], first.Count > 0 ? first[0].TotalCount : 0);
        }

        return new PagedResult<CatalogRow>([], 0);
    }

    public async Task<ItemInfo?> GetItemAsync(int itemId, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<ItemInfo>(
            new CommandDefinition(CatalogSql.ItemSql, new { itemId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MarketBook>> GetMarketBooksAsync(int itemId, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        using var grid = await conn.QueryMultipleAsync(
            new CommandDefinition(CatalogSql.MarketBooksSql, new { itemId }, cancellationToken: cancellationToken));

        var books = (await grid.ReadAsync<MarketBook>()).AsList();
        var levels = (await grid.ReadAsync<LevelRow>()).AsList();

        foreach (var book in books)
        {
            book.Asks = LevelsFor(levels, book.MarketId, "A");
            book.Bids = LevelsFor(levels, book.MarketId, "B");
        }

        return books;
    }

    public async Task<IReadOnlyList<PricePoint>> GetHistoryAsync(
        int itemId, byte marketId, HistoryRange range, CancellationToken cancellationToken)
    {
        var since = CatalogSql.HistoryStart(range, time.GetUtcNow().UtcDateTime);

        await using var conn = CreateConnection();
        var points = (await conn.QueryAsync<PricePointRow>(
            new CommandDefinition(
                CatalogSql.HistorySql,
                new { itemId, marketId, since, limit = HistoryLimit },
                cancellationToken: cancellationToken))).AsList();

        // Queried newest-first so the limit keeps the most recent points; charts want oldest first.
        points.Reverse();
        return points.Select(p => new PricePoint(DateTime.SpecifyKind(p.At, DateTimeKind.Utc), p.Ask, p.Bid, p.LastSale)).ToList();
    }

    public async Task SetWatchedAsync(int itemId, bool watched, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(
            new CommandDefinition(CatalogSql.SetWatchedSql, new { itemId, watched }, cancellationToken: cancellationToken));
    }

    internal static IReadOnlyList<BookLevel> LevelsFor(IEnumerable<LevelRow> rows, byte marketId, string side) =>
        rows.Where(r => r.MarketId == marketId && r.Side == side)
            .OrderBy(r => r.Level)
            .Select(r => new BookLevel(r.Price, r.Quantity))
            .ToList();

    internal sealed class LevelRow
    {
        public byte MarketId { get; set; }

        public string Side { get; set; } = "";

        public short Level { get; set; }

        public decimal Price { get; set; }

        public int Quantity { get; set; }
    }

    private sealed class PricePointRow
    {
        public DateTime At { get; set; }

        public decimal? Ask { get; set; }

        public decimal? Bid { get; set; }

        public decimal? LastSale { get; set; }
    }
}
