-- Steam's order-book endpoint needs a numeric id that is only found on each item's listing page.
-- It never changes, so it is looked up once per item and cached here.
ALTER TABLE dbo.Item ADD SteamNameId BIGINT NULL;

-- A market can have several collection lanes (e.g. steam-sweep and steam-orderbook).
ALTER TABLE dbo.CollectionRun ADD Provider NVARCHAR(32) NULL;
