using System.Data;
using Cs2Prices.Core.Domain;
using Cs2Prices.Core.Providers.Steam;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Cs2Prices.Core.Data;

public sealed record WriteResult(int Considered, int Skipped, int SnapshotsWritten);

/// <summary>Collector-side persistence: runs, quotes, snapshots, order-book depth.</summary>
public sealed class PriceRepository(string connectionString, ILogger<PriceRepository> logger) : ISteamNameIdStore
{
    private SqlConnection CreateConnection() => new(connectionString);

    /// <summary>Market hash names currently flagged as watched (used by providers that poll a subset).</summary>
    public async Task<IReadOnlyList<string>> GetWatchlistAsync(CancellationToken ct)
    {
        await using var conn = CreateConnection();
        var names = await conn.QueryAsync<string>(
            new CommandDefinition("SELECT MarketHashName FROM dbo.Item WHERE IsWatched = 1", cancellationToken: ct));
        return names.AsList();
    }

    public async Task<long> StartRunAsync(byte marketId, string provider, DateTime startedUtc, CancellationToken ct)
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO dbo.CollectionRun (MarketId, Provider, StartedAt, Status)
            OUTPUT INSERTED.Id
            VALUES (@marketId, @provider, @startedUtc, N'Running');
            """,
            new { marketId, provider, startedUtc }, cancellationToken: ct));
    }

    async Task<long?> ISteamNameIdStore.GetAsync(string marketHashName, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT SteamNameId FROM dbo.Item WHERE MarketHashName = @marketHashName",
            new { marketHashName }, cancellationToken: cancellationToken));
    }

    async Task ISteamNameIdStore.SetAsync(string marketHashName, long nameId, CancellationToken cancellationToken)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.Item SET SteamNameId = @nameId WHERE MarketHashName = @marketHashName",
            new { marketHashName, nameId }, cancellationToken: cancellationToken));
    }

    public async Task FinishRunAsync(
        long runId, DateTime finishedUtc, string status, int fetched, int written, int failed, int rateLimitHits,
        string? error, CancellationToken ct)
    {
        await using var conn = CreateConnection();
        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE dbo.CollectionRun
            SET FinishedAt = @finishedUtc, Status = @status, ItemsFetched = @fetched, ItemsWritten = @written,
                ItemsFailed = @failed, RateLimitHits = @rateLimitHits, Error = LEFT(@error, 2000)
            WHERE Id = @runId;
            """,
            new { runId, finishedUtc, status, fetched, written, failed, rateLimitHits, error }, cancellationToken: ct));
    }

    /// <summary>
    /// Persists a batch of quotes from one market in a single transaction:
    /// registers unknown skins, appends a PriceSnapshot row for every item whose values changed
    /// (or whose last snapshot is older than <paramref name="heartbeat"/>), upserts ItemMarketLatest,
    /// and replaces the stored order-book depth of items that arrived with depth.
    /// Non-skin items are ignored.
    /// </summary>
    public async Task<WriteResult> WriteQuotesAsync(
        byte marketId, IReadOnlyCollection<PriceQuote> quotes, TimeSpan heartbeat, CancellationToken ct)
    {
        var (table, levels) = BuildStageTables(quotes);
        if (table.Rows.Count == 0)
            return new WriteResult(quotes.Count, quotes.Count, 0);

        await using var conn = CreateConnection();
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);

        await conn.ExecuteAsync(new CommandDefinition(CreateStageSql, transaction: tx, cancellationToken: ct));

        await BulkCopyAsync(conn, tx, "#Stage", table, ct);
        if (levels.Rows.Count > 0)
            await BulkCopyAsync(conn, tx, "#StageLevels", levels, ct);

        var snapshots = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            ApplyStageSql,
            new { marketId, heartbeatSeconds = (int)heartbeat.TotalSeconds },
            transaction: tx,
            commandTimeout: 300,
            cancellationToken: ct));

        await tx.CommitAsync(ct);

        logger.LogInformation(
            "Wrote {Market}: {Rows} skins staged, {Snapshots} snapshots appended, {Levels} depth levels, {Skipped} non-skin/duplicate rows skipped",
            marketId, table.Rows.Count, snapshots, levels.Rows.Count, quotes.Count - table.Rows.Count);

        return new WriteResult(quotes.Count, quotes.Count - table.Rows.Count, snapshots);
    }

    private static async Task BulkCopyAsync(SqlConnection conn, SqlTransaction tx, string destination, DataTable data, CancellationToken ct)
    {
        using var bulk = new SqlBulkCopy(conn, SqlBulkCopyOptions.Default, tx)
        {
            DestinationTableName = destination,
            BulkCopyTimeout = 300
        };
        foreach (DataColumn column in data.Columns)
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(data, ct);
    }

    internal static (DataTable Quotes, DataTable Levels) BuildStageTables(IEnumerable<PriceQuote> quotes)
    {
        var table = new DataTable();
        table.Columns.Add("MarketHashName", typeof(string));
        table.Columns.Add("Weapon", typeof(string));
        table.Columns.Add("SkinName", typeof(string));
        table.Columns.Add("Wear", typeof(string));
        table.Columns.Add("IsStatTrak", typeof(bool));
        table.Columns.Add("IsSouvenir", typeof(bool));
        table.Columns.Add("Ask", typeof(decimal));
        table.Columns.Add("AskQty", typeof(int));
        table.Columns.Add("Bid", typeof(decimal));
        table.Columns.Add("BidQty", typeof(int));
        table.Columns.Add("LastSale", typeof(decimal));
        table.Columns.Add("Currency", typeof(string));
        table.Columns.Add("CapturedAt", typeof(DateTime));
        table.Columns.Add("HasDepth", typeof(bool));
        table.Columns.Add("IconUrl", typeof(string));
        table.Columns.Add("IconOnly", typeof(bool));

        var levels = new DataTable();
        levels.Columns.Add("MarketHashName", typeof(string));
        levels.Columns.Add("Side", typeof(string));
        levels.Columns.Add("Level", typeof(short));
        levels.Columns.Add("Price", typeof(decimal));
        levels.Columns.Add("Quantity", typeof(int));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var q in quotes)
        {
            var skin = ItemNameParser.TryParseSkin(q.MarketHashName);
            if (skin is null || !seen.Add(q.MarketHashName))
                continue;

            table.Rows.Add(
                q.MarketHashName, skin.Weapon, skin.SkinName, skin.Wear, skin.IsStatTrak, skin.IsSouvenir,
                (object?)q.Ask ?? DBNull.Value, (object?)q.AskQty ?? DBNull.Value,
                (object?)q.Bid ?? DBNull.Value, (object?)q.BidQty ?? DBNull.Value,
                (object?)q.LastSale ?? DBNull.Value, q.Currency,
                // DATETIME2(0): truncate to whole seconds so comparisons are stable.
                new DateTime(q.CapturedAtUtc.Ticks - q.CapturedAtUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc),
                q.Depth is not null,
                (object?)q.IconUrl ?? DBNull.Value,
                q.IconOnly);

            if (q.Depth is null)
                continue;

            AddLevels(levels, q.MarketHashName, "A", q.Depth.Asks);
            AddLevels(levels, q.MarketHashName, "B", q.Depth.Bids);
        }

        return (table, levels);
    }

    private static void AddLevels(DataTable levels, string name, string side, IReadOnlyList<BookLevel> source)
    {
        for (var i = 0; i < source.Count; i++)
            levels.Rows.Add(name, side, (short)(i + 1), source[i].Price, source[i].Quantity);
    }

    internal const string CreateStageSql = """
        CREATE TABLE #Stage (
            MarketHashName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL PRIMARY KEY,
            Weapon         NVARCHAR(64)  COLLATE DATABASE_DEFAULT NOT NULL,
            SkinName       NVARCHAR(128) COLLATE DATABASE_DEFAULT NOT NULL,
            Wear           NVARCHAR(32)  COLLATE DATABASE_DEFAULT NOT NULL,
            IsStatTrak     BIT NOT NULL,
            IsSouvenir     BIT NOT NULL,
            Ask            DECIMAL(18,4) NULL,
            AskQty         INT NULL,
            Bid            DECIMAL(18,4) NULL,
            BidQty         INT NULL,
            LastSale       DECIMAL(18,4) NULL,
            Currency       CHAR(3) NOT NULL,
            CapturedAt     DATETIME2(0) NOT NULL,
            HasDepth       BIT NOT NULL DEFAULT (0),
            IconUrl        NVARCHAR(512) COLLATE DATABASE_DEFAULT NULL,
            IconOnly       BIT NOT NULL DEFAULT (0),
            ItemId         INT NULL,
            WriteSnapshot  BIT NOT NULL DEFAULT (0)
        );

        CREATE TABLE #StageLevels (
            MarketHashName NVARCHAR(256) COLLATE DATABASE_DEFAULT NOT NULL,
            Side           CHAR(1) NOT NULL,
            Level          SMALLINT NOT NULL,
            Price          DECIMAL(18,4) NOT NULL,
            Quantity       INT NOT NULL
        );
        """;

    // Returns the number of snapshot rows appended.
    internal const string ApplyStageSql = """
        -- 1. Register skins we have not seen before.
        INSERT INTO dbo.Item (MarketHashName, ItemType, Weapon, SkinName, Wear, IsStatTrak, IsSouvenir, IconUrl)
        SELECT s.MarketHashName, N'Skin', s.Weapon, s.SkinName, s.Wear, s.IsStatTrak, s.IsSouvenir, s.IconUrl
        FROM #Stage s
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Item i WHERE i.MarketHashName = s.MarketHashName);

        -- Keep the picture current for items the market described (only some lanes carry one).
        UPDATE i SET i.IconUrl = s.IconUrl
        FROM dbo.Item i
        JOIN #Stage s ON s.MarketHashName = i.MarketHashName
        WHERE s.IconUrl IS NOT NULL AND (i.IconUrl IS NULL OR i.IconUrl <> s.IconUrl);

        -- 2. Resolve item ids and decide which rows deserve a new snapshot.
        UPDATE s
        SET s.ItemId = i.Id,
            s.WriteSnapshot = CASE
                WHEN s.IconOnly = 1 THEN 0
                WHEN l.ItemId IS NULL THEN 1
                WHEN l.LastSnapshotAt <= DATEADD(SECOND, -@heartbeatSeconds, s.CapturedAt) THEN 1
                WHEN EXISTS (SELECT s.Ask, s.AskQty, s.Bid, s.BidQty, s.LastSale
                             EXCEPT
                             SELECT l.Ask, l.AskQty, l.Bid, l.BidQty, l.LastSale) THEN 1
                ELSE 0 END
        FROM #Stage s
        JOIN dbo.Item i ON i.MarketHashName = s.MarketHashName
        LEFT JOIN dbo.ItemMarketLatest l ON l.ItemId = i.Id AND l.MarketId = @marketId;

        -- 3. Append snapshots.
        INSERT INTO dbo.PriceSnapshot (ItemId, MarketId, CapturedAt, Ask, AskQty, Bid, BidQty, LastSale, Currency)
        SELECT s.ItemId, @marketId, s.CapturedAt, s.Ask, s.AskQty, s.Bid, s.BidQty, s.LastSale, s.Currency
        FROM #Stage s
        WHERE s.WriteSnapshot = 1 AND s.IconOnly = 0
          AND NOT EXISTS (SELECT 1 FROM dbo.PriceSnapshot p
                          WHERE p.ItemId = s.ItemId AND p.MarketId = @marketId AND p.CapturedAt = s.CapturedAt);

        DECLARE @snapshots INT = @@ROWCOUNT;

        -- 4. Upsert current state.
        MERGE dbo.ItemMarketLatest AS t
        USING (SELECT * FROM #Stage WHERE ItemId IS NOT NULL AND IconOnly = 0) AS s
            ON t.ItemId = s.ItemId AND t.MarketId = @marketId
        WHEN MATCHED THEN UPDATE SET
            Ask = s.Ask, AskQty = s.AskQty, Bid = s.Bid, BidQty = s.BidQty, LastSale = s.LastSale,
            Currency = s.Currency, CapturedAt = s.CapturedAt,
            LastSnapshotAt = CASE WHEN s.WriteSnapshot = 1 THEN s.CapturedAt ELSE t.LastSnapshotAt END
        WHEN NOT MATCHED THEN INSERT
            (ItemId, MarketId, Ask, AskQty, Bid, BidQty, LastSale, Currency, CapturedAt, LastSnapshotAt)
            VALUES (s.ItemId, @marketId, s.Ask, s.AskQty, s.Bid, s.BidQty, s.LastSale, s.Currency, s.CapturedAt, s.CapturedAt);

        -- 5. The stored ladder always belongs to the latest quote: drop it for every item in this batch, then
        --    store the new one for items that arrived with depth. A later quote without depth (e.g. the hourly
        --    sweep) therefore clears a ladder that is no longer current.
        DELETE l
        FROM dbo.OrderBookLevel l
        WHERE l.MarketId = @marketId
          AND l.ItemId IN (SELECT s.ItemId FROM #Stage s WHERE s.ItemId IS NOT NULL AND s.IconOnly = 0);

        INSERT INTO dbo.OrderBookLevel (ItemId, MarketId, Side, Level, Price, Quantity, CapturedAt)
        SELECT s.ItemId, @marketId, d.Side, d.Level, d.Price, d.Quantity, s.CapturedAt
        FROM #StageLevels d
        JOIN #Stage s ON s.MarketHashName = d.MarketHashName
        WHERE s.ItemId IS NOT NULL AND s.IconOnly = 0;

        DROP TABLE #StageLevels;
        DROP TABLE #Stage;

        SELECT @snapshots;
        """;
}
