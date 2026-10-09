using Cs2Prices.Core;
using Cs2Prices.Core.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cs2Prices.Tests;

public class ServiceRegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Cs2Prices"] = "Server=localhost;Database=x;Trusted_Connection=True;TrustServerCertificate=True"
        };
        foreach (var (k, v) in overrides ?? [])
            settings[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCs2PricesData(config);
        services.AddMarketProviders(config);
        return services.BuildServiceProvider(validateScopes: true);
    }

    [Fact]
    public void Registers_every_lane_by_default()
    {
        using var sp = Build();

        var codes = sp.GetServices<IMarketProvider>().Select(p => p.Code).Order().ToArray();

        Assert.Equal(["skinport", "steam-orderbook", "steam-sweep"], codes);
    }

    [Fact]
    public void Steam_lanes_share_one_throttle()
    {
        using var sp = Build();

        Assert.Same(
            sp.GetRequiredService<Cs2Prices.Core.Providers.Steam.SteamThrottle>(),
            sp.GetRequiredService<Cs2Prices.Core.Providers.Steam.SteamThrottle>());
        Assert.Equal(2, sp.GetServices<IMarketProvider>().Count(p => p.MarketId == Cs2Prices.Core.Domain.MarketIds.Steam));
    }

    [Fact]
    public void Lanes_can_be_switched_off_in_configuration()
    {
        using var sp = Build(new()
        {
            ["Providers:Skinport:Enabled"] = "false",
            ["Providers:Steam:OrderBookEnabled"] = "false"
        });

        var codes = sp.GetServices<IMarketProvider>().Select(p => p.Code).ToArray();

        Assert.Equal(["steam-sweep"], codes);
    }

    [Fact]
    public void Poll_intervals_come_from_configuration()
    {
        using var sp = Build(new()
        {
            ["Providers:Steam:SweepInterval"] = "02:00:00",
            ["Providers:Steam:OrderBookInterval"] = "00:10:00"
        });

        var byCode = sp.GetServices<IMarketProvider>().ToDictionary(p => p.Code);

        Assert.Equal(TimeSpan.FromHours(2), byCode["steam-sweep"].PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(10), byCode["steam-orderbook"].PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), byCode["skinport"].PollInterval);
    }
}
