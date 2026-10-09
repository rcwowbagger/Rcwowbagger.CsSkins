using System.Net;
using System.Threading.RateLimiting;
using Cs2Prices.Core.Catalog;
using Cs2Prices.Core.Data;
using Cs2Prices.Core.Providers;
using Cs2Prices.Core.Providers.Skinport;
using Cs2Prices.Core.Providers.Steam;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Polly.Retry;

namespace Cs2Prices.Core;

public static class ServiceCollectionExtensions
{
    public const string ConnectionStringName = "Cs2Prices";

    private const string UserAgent = "Cs2Prices/1.0 (personal price tracker)";

    /// <summary>Registers the repository (Dapper) used by both the collector and the web app.</summary>
    public static IServiceCollection AddCs2PricesData(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is missing.");

        services.AddSingleton(sp => new PriceRepository(connectionString, sp.GetRequiredService<ILogger<PriceRepository>>()));
        services.AddSingleton<ISteamNameIdStore>(sp => sp.GetRequiredService<PriceRepository>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICatalogQueries>(sp => new CatalogQueries(connectionString, sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    public static string GetCs2PricesConnectionString(this IConfiguration configuration) =>
        configuration.GetConnectionString(ConnectionStringName)
        ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is missing.");

    /// <summary>Registers every enabled market provider with its own rate limiting and retry pipeline.</summary>
    public static IServiceCollection AddMarketProviders(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        AddSkinport(services, configuration);
        AddSteam(services, configuration);
        return services;
    }

    private static void AddSkinport(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Providers:Skinport");
        services.Configure<SkinportOptions>(section);
        var skinport = section.Get<SkinportOptions>() ?? new SkinportOptions();
        if (!skinport.Enabled)
            return;

        // Skinport documents roughly 8 requests per 5 minutes; stay under it with margin.
        var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 6,
            TokensPerPeriod = 6,
            ReplenishmentPeriod = TimeSpan.FromMinutes(5),
            AutoReplenishment = true,
            QueueLimit = 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });

        var skinportClient = services.AddHttpClient<SkinportProvider>(client =>
            {
                client.BaseAddress = new Uri(skinport.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateDecompressingHandler);

        // Handlers run in registration order, outermost first: the resilience pipeline wraps the limiter,
        // so every retry is rate limited too.
        skinportClient.AddStandardResilienceHandler(o =>
        {
            // The full item list is large; give it room, and retry sparingly.
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(90);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(4);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(3);
            o.Retry.MaxRetryAttempts = 2;
            o.Retry.Delay = TimeSpan.FromSeconds(20);
            o.Retry.ShouldHandle = RetryOnTransientFailures;
        });
        skinportClient.AddHttpMessageHandler(() => new RateLimitingHandler(limiter));

        services.AddTransient<IMarketProvider>(sp => sp.GetRequiredService<SkinportProvider>());
    }

    private static void AddSteam(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Providers:Steam");
        services.Configure<SteamOptions>(section);
        var steam = section.Get<SteamOptions>() ?? new SteamOptions();
        if (!steam.Enabled)
            return;

        // One throttle for every Steam request, whichever lane makes it.
        services.AddSingleton(sp => new SteamThrottle(steam.MinRequestSpacing, sp.GetRequiredService<TimeProvider>()));

        if (steam.SweepEnabled)
        {
            ConfigureSteamClient(services.AddHttpClient<SteamSweepProvider>(), steam);
            services.AddTransient<IMarketProvider>(sp => sp.GetRequiredService<SteamSweepProvider>());
        }

        if (steam.OrderBookEnabled)
        {
            ConfigureSteamClient(services.AddHttpClient<SteamOrderBookProvider>(), steam);
            services.AddTransient<IMarketProvider>(sp => sp.GetRequiredService<SteamOrderBookProvider>());
        }
    }

    private static void ConfigureSteamClient(IHttpClientBuilder builder, SteamOptions steam)
    {
        builder
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = new Uri(steam.BaseUrl);
                client.Timeout = Timeout.InfiniteTimeSpan; // the resilience pipeline owns timeouts
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            })
            .ConfigurePrimaryHttpMessageHandler(CreateDecompressingHandler);

        builder.AddStandardResilienceHandler(o =>
        {
            // 429 is handled by SteamThrottle (shared cooldown), not by blind retries.
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(2);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(1);
            o.Retry.MaxRetryAttempts = 2;
            o.Retry.Delay = TimeSpan.FromSeconds(5);
            o.Retry.ShouldHandle = RetryOnTransientFailures;
        });
    }

    private static HttpMessageHandler CreateDecompressingHandler() => new SocketsHttpHandler
    {
        // Skinport requires Brotli; All adds br, gzip and deflate.
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    private static ValueTask<bool> RetryOnTransientFailures(RetryPredicateArguments<HttpResponseMessage> args) =>
        ValueTask.FromResult(
            args.Outcome.Exception is HttpRequestException ||
            args.Outcome.Result is { StatusCode: >= HttpStatusCode.InternalServerError });
}
