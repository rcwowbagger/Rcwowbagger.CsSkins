using System.Reflection;
using System.Text.RegularExpressions;
using Cs2Prices.Core.Catalog;
using Cs2Prices.Core.Data;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Cs2Prices.Tests;

/// <summary>Parses every SQL statement we ship with the T-SQL parser, so syntax slips fail here and not at first run.</summary>
public class SqlSyntaxTests
{
    private static IReadOnlyList<string> Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        parser.Parse(reader, out var errors);
        return errors.Select(e => $"line {e.Line}: {e.Message}").ToList();
    }

    public static IEnumerable<object[]> Migrations() =>
        typeof(DatabaseMigrator).Assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".Migrations.Scripts.", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Migrations))]
    public void Migration_script_is_valid_tsql(string resource)
    {
        using var stream = typeof(DatabaseMigrator).Assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();

        // DbUp treats GO as a batch separator; the parser does not know it.
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            Assert.Empty(Parse(batch));
    }

    [Fact]
    public void Migrations_are_numbered_without_gaps()
    {
        var names = Migrations().Select(m => (string)m[0]).ToList();
        Assert.True(names.Count >= 4);
        Assert.Contains(names, n => n.Contains("0004_", StringComparison.Ordinal));
    }

    [Fact]
    public void Stage_statements_are_valid_tsql()
    {
        Assert.Empty(Parse(PriceRepository.CreateStageSql));
        Assert.Empty(Parse(PriceRepository.ApplyStageSql));
    }

    [Fact]
    public void Fixed_catalog_statements_are_valid_tsql()
    {
        Assert.Empty(Parse(CatalogSql.MarketsSql));
        Assert.Empty(Parse(CatalogSql.ItemSql));
        Assert.Empty(Parse(CatalogSql.MarketBooksSql));
        Assert.Empty(Parse(CatalogSql.HistorySql));
        Assert.Empty(Parse(CatalogSql.SetWatchedSql));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ak redline", false)]
    [InlineData("ak47 100%_[x] it's", true)]
    public void Search_statement_is_valid_for_every_sort(string? search, bool watched)
    {
        foreach (var sort in Enum.GetValues<CatalogSort>())
        foreach (var desc in new[] { false, true })
        {
            var (sql, _) = CatalogSql.BuildSearch(new CatalogQuery(1, search, sort, desc, 100, 50, watched));
            Assert.Empty(Parse(sql));
        }
    }
}
