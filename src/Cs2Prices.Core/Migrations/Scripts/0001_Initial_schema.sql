CREATE TABLE dbo.Market (
    Id        TINYINT       NOT NULL CONSTRAINT PK_Market PRIMARY KEY,
    Code      NVARCHAR(32)  NOT NULL CONSTRAINT UQ_Market_Code UNIQUE,
    Name      NVARCHAR(100) NOT NULL,
    Currency  CHAR(3)       NOT NULL,
    -- Fraction of the sale price the seller pays as fee (informational, for net-price views later)
    SellerFeeRate DECIMAL(6,4) NULL,
    IsEnabled BIT           NOT NULL CONSTRAINT DF_Market_IsEnabled DEFAULT (1)
);

CREATE TABLE dbo.Item (
    Id             INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Item PRIMARY KEY,
    -- Canonical cross-market key, e.g. "AK-47 | Redline (Field-Tested)"
    MarketHashName NVARCHAR(256) NOT NULL,
    ItemType       NVARCHAR(32)  NOT NULL CONSTRAINT DF_Item_Type DEFAULT ('Skin'),
    Weapon         NVARCHAR(64)  NULL,
    SkinName       NVARCHAR(128) NULL,
    Wear           NVARCHAR(32)  NULL,
    IsStatTrak     BIT           NOT NULL CONSTRAINT DF_Item_StatTrak DEFAULT (0),
    IsSouvenir     BIT           NOT NULL CONSTRAINT DF_Item_Souvenir DEFAULT (0),
    IconUrl        NVARCHAR(512) NULL,
    IsWatched      BIT           NOT NULL CONSTRAINT DF_Item_Watched DEFAULT (0),
    CreatedAt      DATETIME2(0)  NOT NULL CONSTRAINT DF_Item_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT UQ_Item_MarketHashName UNIQUE (MarketHashName)
);

CREATE INDEX IX_Item_Weapon ON dbo.Item (Weapon, SkinName) INCLUDE (Wear);
CREATE INDEX IX_Item_Watched ON dbo.Item (IsWatched) WHERE IsWatched = 1;

-- Current state: one row per (item, market). Upserted on every poll.
CREATE TABLE dbo.ItemMarketLatest (
    ItemId     INT          NOT NULL,
    MarketId   TINYINT      NOT NULL,
    Ask        DECIMAL(18,4) NULL,   -- lowest sell listing
    AskQty     INT          NULL,
    Bid        DECIMAL(18,4) NULL,   -- highest buy order
    BidQty     INT          NULL,
    LastSale   DECIMAL(18,4) NULL,
    Currency   CHAR(3)      NOT NULL,
    CapturedAt DATETIME2(0) NOT NULL,   -- last successful poll that saw this item
    LastSnapshotAt DATETIME2(0) NOT NULL, -- last time a PriceSnapshot row was written (heartbeat logic)
    CONSTRAINT PK_ItemMarketLatest PRIMARY KEY (ItemId, MarketId),
    CONSTRAINT FK_IML_Item   FOREIGN KEY (ItemId)   REFERENCES dbo.Item (Id),
    CONSTRAINT FK_IML_Market FOREIGN KEY (MarketId) REFERENCES dbo.Market (Id)
);

CREATE INDEX IX_IML_Market ON dbo.ItemMarketLatest (MarketId) INCLUDE (CapturedAt);

-- Append-only time series. Written only when values change or a heartbeat is due.
CREATE TABLE dbo.PriceSnapshot (
    ItemId     INT          NOT NULL,
    MarketId   TINYINT      NOT NULL,
    CapturedAt DATETIME2(0) NOT NULL,
    Ask        DECIMAL(18,4) NULL,
    AskQty     INT          NULL,
    Bid        DECIMAL(18,4) NULL,
    BidQty     INT          NULL,
    LastSale   DECIMAL(18,4) NULL,
    Currency   CHAR(3)      NOT NULL,
    CONSTRAINT PK_PriceSnapshot PRIMARY KEY CLUSTERED (ItemId, MarketId, CapturedAt),
    CONSTRAINT FK_PS_Item   FOREIGN KEY (ItemId)   REFERENCES dbo.Item (Id),
    CONSTRAINT FK_PS_Market FOREIGN KEY (MarketId) REFERENCES dbo.Market (Id)
);

CREATE INDEX IX_PriceSnapshot_Time ON dbo.PriceSnapshot (CapturedAt) INCLUDE (ItemId, MarketId);

-- Daily aggregated sales where a market exposes them (Skinport, Steam).
CREATE TABLE dbo.SaleDaily (
    ItemId    INT          NOT NULL,
    MarketId  TINYINT      NOT NULL,
    SaleDate  DATE         NOT NULL,
    MinPrice  DECIMAL(18,4) NULL,
    MaxPrice  DECIMAL(18,4) NULL,
    AvgPrice  DECIMAL(18,4) NULL,
    MedianPrice DECIMAL(18,4) NULL,
    Volume    INT          NOT NULL,
    Currency  CHAR(3)      NOT NULL,
    CONSTRAINT PK_SaleDaily PRIMARY KEY (ItemId, MarketId, SaleDate),
    CONSTRAINT FK_SD_Item   FOREIGN KEY (ItemId)   REFERENCES dbo.Item (Id),
    CONSTRAINT FK_SD_Market FOREIGN KEY (MarketId) REFERENCES dbo.Market (Id)
);

CREATE TABLE dbo.CollectionRun (
    Id            BIGINT IDENTITY(1,1) NOT NULL CONSTRAINT PK_CollectionRun PRIMARY KEY,
    MarketId      TINYINT      NOT NULL,
    StartedAt     DATETIME2(0) NOT NULL,
    FinishedAt    DATETIME2(0) NULL,
    Status        NVARCHAR(16) NOT NULL,  -- Running | Succeeded | Failed | Partial
    ItemsFetched  INT          NOT NULL CONSTRAINT DF_CR_Fetched DEFAULT (0),
    ItemsWritten  INT          NOT NULL CONSTRAINT DF_CR_Written DEFAULT (0),
    ItemsFailed   INT          NOT NULL CONSTRAINT DF_CR_Failed  DEFAULT (0),
    RateLimitHits INT          NOT NULL CONSTRAINT DF_CR_RL      DEFAULT (0),
    Error         NVARCHAR(2000) NULL,
    CONSTRAINT FK_CR_Market FOREIGN KEY (MarketId) REFERENCES dbo.Market (Id)
);

CREATE INDEX IX_CollectionRun_Market ON dbo.CollectionRun (MarketId, StartedAt DESC);
