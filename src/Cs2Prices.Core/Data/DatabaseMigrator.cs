using DbUp;
using Microsoft.Extensions.Logging;

namespace Cs2Prices.Core.Data;

public static class DatabaseMigrator
{
    /// <summary>Creates the database if missing, then applies any embedded scripts not yet journaled.</summary>
    public static void Migrate(string connectionString, ILogger logger)
    {
        EnsureDatabase.For.SqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(DatabaseMigrator).Assembly,
                name => name.Contains(".Migrations.Scripts.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new DbUpLogger(logger))
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
            throw new InvalidOperationException("Database migration failed.", result.Error);
    }
}
