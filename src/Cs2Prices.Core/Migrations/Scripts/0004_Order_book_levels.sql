-- Current order-book depth per item and market: the price ladder behind the best bid/ask.
-- Replaced wholesale on every poll that carries depth (only markets that publish depth, e.g. Steam).
CREATE TABLE dbo.OrderBookLevel (
    ItemId     INT          NOT NULL,
    MarketId   TINYINT      NOT NULL,
    Side       CHAR(1)      NOT NULL,   -- 'A' = ask (sell), 'B' = bid (buy)
    Level      SMALLINT     NOT NULL,   -- 1 = best price on that side
    Price      DECIMAL(18,4) NOT NULL,
    Quantity   INT          NOT NULL,
    CapturedAt DATETIME2(0) NOT NULL,
    CONSTRAINT PK_OrderBookLevel PRIMARY KEY (ItemId, MarketId, Side, Level),
    CONSTRAINT CK_OrderBookLevel_Side CHECK (Side IN ('A', 'B')),
    CONSTRAINT FK_OBL_Item   FOREIGN KEY (ItemId)   REFERENCES dbo.Item (Id),
    CONSTRAINT FK_OBL_Market FOREIGN KEY (MarketId) REFERENCES dbo.Market (Id)
);
