# API keys: which markets need one, and how to get it

| Market | Key needed? | Gives you | Status |
|---|---|---|---|
| Skinport | No | Lowest ask + listing count (no bid) | Built |
| Steam Community Market | No | Sweep: ask + listing count for every skin. Order book: real bid + ask (watchlist) | Built |
| CSFloat | **Yes** | Listings (ask). Buy orders are not documented in the public API | Waiting for key |
| DMarket | **Yes** | Offers and buy orders (bid + ask) | Waiting for keys |

Keys never go into `appsettings.json` or git. Both apps share one user-secrets store
(the `UserSecretsId` already in the two `.csproj` files, which is left untouched), so you set a secret once
and the collector and the web app both see it.

```
dotnet user-secrets set "Providers:CsFloat:ApiKey" "<your key>" --project src/Cs2Prices.Collector
dotnet user-secrets list --project src/Cs2Prices.Collector
```

Secrets live outside the repo, in `%APPDATA%\Microsoft\UserSecrets\<UserSecretsId>\secrets.json`.
They are only loaded when `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` is `Development`, which is what
`dotnet run` and Visual Studio use by default.

You can also move the SQL Server password out of `appsettings.json` the same way:

```
dotnet user-secrets set "ConnectionStrings:Cs2Prices" "Server=localhost,1433;Database=Cs2Prices;User Id=sa;Password=...;TrustServerCertificate=True;Encrypt=True" --project src/Cs2Prices.Collector
```

## CSFloat

1. Sign in at https://csfloat.com with your Steam account.
2. Open your profile and go to the **Developer** tab.
3. Create an API key and copy it.
4. Store it: `dotnet user-secrets set "Providers:CsFloat:ApiKey" "<key>" --project src/Cs2Prices.Collector`

How it is used: send the key in the `Authorization` header (the raw key, no `Bearer` prefix). Listings can be
queried per `market_hash_name`, max 50 per request. The docs give no numeric rate limit, only HTTP 429 when you
go too fast, so the provider will be conservative.

Open question to settle when this is built: the public docs describe listings only, so CSFloat may give an ask
side only. If buy orders turn out to be exposed, they would fill the bid side.

## DMarket

1. Sign in at https://dmarket.com and open your **account settings**.
2. Find the **API** section and generate an API key pair.
3. Copy the **public key** and the **secret key**. The secret key is typically shown only once, so save it straight away.
4. Store both:
   ```
   dotnet user-secrets set "Providers:DMarket:PublicKey" "<public key>" --project src/Cs2Prices.Collector
   dotnet user-secrets set "Providers:DMarket:SecretKey" "<secret key>" --project src/Cs2Prices.Collector
   ```

How it is used: DMarket's documentation says every request must carry three headers: `X-Api-Key` (the public
key), `X-Sign-Date` (Unix timestamp in seconds, within 2 minutes of server time) and `X-Request-Sign`
(an Ed25519 signature with the prefix `dmar ed25519`, made with the secret key). The provider signs each request itself.

I could not confirm whether the aggregated-prices endpoint (best bid and ask per title) is exempt from
signing, so DMarket is treated as needing keys.

## Not planned

Buff163 has no official API and scraping it breaks its terms and breaks often. Paid aggregators
(Pricempire, SteamWebAPI, CSPriceAPI) were ruled out.
