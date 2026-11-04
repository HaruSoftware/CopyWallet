namespace WalletCopy.Models;

public sealed class ClobOrderRequest
{
    public string TokenId { get; init; } = "";
    public string ConditionId { get; init; } = "";
    public string MarketTitle { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string Side { get; init; } = "BUY";
    public string OrderType { get; init; } = "FOK";
    public decimal Size { get; init; }
    public decimal Price { get; init; }
    public decimal MaxSlippagePercent { get; init; }
    public bool IsNegRisk { get; init; }
    public long Expiration { get; init; }
}

public sealed class ClobOrderResponse
{
    public bool Success { get; set; }
    public string OrderId { get; set; } = "";
    public string Status { get; set; } = "";
    public string ErrorMessage { get; set; } = "";
    public decimal ExecutedSize { get; set; }
    public decimal ExecutedPrice { get; set; }
    public decimal TotalCost { get; set; }
    public string RawJson { get; set; } = "";
}

public sealed class ClobApiCredentials
{
    public string ApiKey { get; set; } = "";
    public string Secret { get; set; } = "";
    public string Passphrase { get; set; } = "";
}

public sealed class ClobOpenOrder
{
    public string Id { get; set; } = "";
    public string Market { get; set; } = "";
    public string AssetId { get; set; } = "";
    public string Side { get; set; } = "";
    public decimal Price { get; set; }
    public decimal OriginalSize { get; set; }
    public decimal SizeMatched { get; set; }
    public decimal RemainingSize => Math.Max(0m, OriginalSize - SizeMatched);
    public string OrderType { get; set; } = "GTC";
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = "Open";
}

