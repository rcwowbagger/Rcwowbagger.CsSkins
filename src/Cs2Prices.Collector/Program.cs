using Cs2Prices.Collector;
using Cs2Prices.Core;
using Cs2Prices.Core.Data;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    builder.Services.AddSerilog((services, lc) => lc
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    builder.Services.AddCs2PricesData(builder.Configuration);
    builder.Services.AddMarketProviders(builder.Configuration);
    builder.Services.AddHostedService<MarketPollingService>();

    var host = builder.Build();

    // Apply schema migrations before polling starts.
    DatabaseMigrator.Migrate(
        builder.Configuration.GetCs2PricesConnectionString(),
        host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations"));

    host.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Collector terminated unexpectedly");
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

return 0;
