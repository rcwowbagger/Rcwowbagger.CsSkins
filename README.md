# Cs2Prices

Collects Counter-Strike 2 weapon-skin prices (asks, bids, last sales) from several markets into SQL Server
and presents a catalog with price history.

## Layout
- `src/Cs2Prices.Core`: domain, `IMarketProvider`, Dapper data access, DbUp migrations
- `src/Cs2Prices.Collector`: worker service, one polling loop per provider lane
- `src/Cs2Prices.Web`: Blazor Interactive Server + Radzen UI (catalog pages still to come)
- `tests/Cs2Prices.Tests`: xUnit

## Run
Apps run natively on Windows; only SQL Server runs in Docker.
```
docker compose up -d
dotnet run --project src/Cs2Prices.Collector
dotnet run --project src/Cs2Prices.Web
```
The collector owns the schema: it creates the database and applies DbUp migrations on startup,
so start it before the web app. Secrets and API keys: see [docs/API-KEYS.md](docs/API-KEYS.md).

## Providers
| Lane | Key? | Interval | What it records |
|---|---|---|---|
| `skinport` | no | 5 min | Lowest ask + listing count, whole market in one request |
| `steam-sweep` | no | 1 h | Lowest ask + listing count for every skin, 100 per request, rate limited |
| `steam-orderbook` | no | 5 min | **Bid and ask** with top-of-book quantity, for watched items only |
| `csfloat`, `dmarket` | yes | | Waiting for keys |

Steam prices arrive in your own Steam currency (the order-book endpoint cannot be asked for another one), so
set `Providers:Steam:CurrencyId` / `CurrencyCode` in the Collector's `appsettings.json` to match (CHF is 4).
The collector logs a warning if the order book answers in a different currency.

Steam throttles hard (about 15 requests a minute, then 429). Both Steam lanes share one throttle
(`MinRequestSpacing`, default 4.5 s) and a shared cooldown after a 429. A full sweep of about 35k market
items takes roughly 25 minutes and saves progress every 10 pages. Intervals and switches are in
`src/Cs2Prices.Collector/appsettings.json` under `Providers`.

### Choosing items for the Steam order book
The order-book lane only reads items flagged as watched (so bid/ask spread and the full ladder are available
for them). Click the ☆ in the catalog, or the Watch button on the item page.
Watched items are skipped by the sweep, since the order-book lane records both sides for them.
If more than 40 are watched, each poll covers the next 40 round-robin.

## Web UI
`dotnet run --project src/Cs2Prices.Web`
* **Catalog** (`/`): pick a market, search (words are ANDed, `ak47` finds `AK-47`), sort and page server-side.
  Search, market and the watched filter live in the URL, so Back from an item restores them.
* **Item page** (`/item/{id}`): one order-book card per market (bid, ask, spread, last sale; the price ladder
  where the market publishes depth, currently watched Steam items) and a price-history chart per market.
* A consolidated all-markets view is planned; it needs currency conversion (Skinport is EUR, Steam USD).
* Live ticking is planned; `ItemDetail.LoadAsync` is the refresh hook.

## Data
`ItemMarketLatest` holds the current state per item and market. `PriceSnapshot` is an append-only history,
written when a value changes or once an hour for unchanged prices. `CollectionRun` logs every poll with the
lane in its `Provider` column.

## Tests
`dotnet test`. Provider parsing is tested against fixtures, all SQL is parsed with the T-SQL parser, and the
pages are tested with bUnit against a fake catalog. The Steam order-book fixture is a representative
response, not a recording; confirm the first live poll in the logs.
