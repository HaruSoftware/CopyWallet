namespace WalletCopy.Models;

public sealed class MarketOutcome
{
    public string Name { get; set; } = "";
    public string TokenId { get; set; } = "";
    public decimal Price { get; set; }
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
}

public sealed class PaperMarket
{
    public string Id { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Slug { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public decimal Volume24h { get; set; }
    public DateTime? EndDate { get; set; }
    public bool IsClosed { get; set; }
    public List<MarketOutcome> Outcomes { get; set; } = [];
    public string VolumeLabel => $"${Volume24h:N0} vol. 24h";
}

public sealed class PaperOrderRequest
{
    public string MarketId { get; init; } = "";
    public string ConditionId { get; init; } = "";
    public string MarketTitle { get; init; } = "";
    public string TokenId { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string Side { get; init; } = "BUY";
    public string OrderType { get; init; } = "FOK";
    public decimal Shares { get; init; }
    public decimal Price { get; init; }
    public decimal MaxSlippagePercent { get; init; }
    public bool IsSplit { get; init; }
}

public sealed class PaperOrder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string MarketId { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string MarketTitle { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Side { get; set; } = "BUY";
    public string OrderType { get; set; } = "FOK";
    public decimal Shares { get; set; }
    public decimal Price { get; set; }
    public decimal LimitPrice { get; set; }
    public decimal FilledShares { get; set; }
    public decimal RemainingShares { get; set; }
    public decimal TotalCost { get; set; }
    public decimal ReservedCash { get; set; }
    public decimal MaxSlippagePercent { get; set; }
    public bool IsSplit { get; set; }
    public string Status { get; set; } = "Filled";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string StatusLabel => Status == "Cancelled"
        ? $"Cancelada · {FilledShares:N2} shares executadas"
        : $"{Status} · {FilledShares:N2}/{Shares:N2} shares · ${TotalCost:N2}";
    public string TradeLabel => $"{Side} · {Outcome} · {OrderType}";
}

public sealed class PaperPosition
{
    public string ConditionId { get; set; } = "";
    public string MarketTitle { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string Outcome { get; set; } = "";
    public decimal Shares { get; set; }
    public decimal AveragePrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public string Status { get; set; } = "Open";
    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAt { get; set; }
    public decimal CostBasis => Shares * AveragePrice;
    public decimal MarketValue => Shares * CurrentPrice;
    public decimal UnrealizedPnl => MarketValue - CostBasis;
    public string PositionLabel => $"{Shares:N2} {Outcome} @ {AveragePrice:P1}";
    public string ValueLabel => $"${MarketValue:N2}";
    public string PnlLabel => $"{(Status == "Open" ? UnrealizedPnl : RealizedPnl):+$#,0.00;-$#,0.00;$0.00}";
}

public sealed class PaperAccount
{
    public decimal StartingBalance { get; set; } = 10_000m;
    public decimal Balance { get; set; } = 10_000m;
    public decimal MaxTradeValue { get; set; } = 500m;
    public decimal MaxExposure { get; set; } = 2_000m;
    public decimal DefaultSlippagePercent { get; set; } = 1m;
    public bool RequireConfirmation { get; set; } = true;
    public List<PaperOrder> Orders { get; set; } = [];
    public List<PaperPosition> Positions { get; set; } = [];
    public decimal RealizedPnl { get; set; }
}

public sealed class OrderBookLevel
{
    public decimal Price { get; init; }
    public decimal Shares { get; init; }
}

public sealed class MarketOrderBook
{
    public List<OrderBookLevel> Bids { get; init; } = [];
    public List<OrderBookLevel> Asks { get; init; } = [];
    public decimal? BestBidOverride { get; init; }
    public decimal? BestAskOverride { get; init; }
    public decimal BestBid => BestBidOverride ?? (Bids.Count == 0 ? 0m : Bids.Max(level => level.Price));
    public decimal BestAsk => BestAskOverride ?? (Asks.Count == 0 ? 0m : Asks.Min(level => level.Price));
    public string SpreadLabel => BestBid > 0m && BestAsk > 0m
        ? $"{(BestAsk - BestBid):P2}"
        : "—";
}
