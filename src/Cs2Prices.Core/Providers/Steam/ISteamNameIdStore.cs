namespace Cs2Prices.Core.Providers.Steam;

/// <summary>
/// Steam's order-book endpoint needs a numeric item_nameid that is only found on the item's listing page.
/// It never changes, so it is looked up once per item and stored.
/// </summary>
public interface ISteamNameIdStore
{
    Task<long?> GetAsync(string marketHashName, CancellationToken cancellationToken);

    Task SetAsync(string marketHashName, long nameId, CancellationToken cancellationToken);
}
