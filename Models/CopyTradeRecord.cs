namespace WalletCopy.Models;

public sealed class CopyTradeRecord
{
    public string Key { get; set; } = "";
    public string WalletAddress { get; set; } = "";
    public string MarketTitle { get; set; } = "";
    public string Side { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string AmountLabel { get; set; } = "";
    public string CopyMode { get; set; } = "Automatico";
    public string ExecutionTarget { get; set; } = "Real";
    public bool IsLiveOrder { get; set; }
    public string ClobOrderId { get; set; } = "";
    public string ExecutionStatus { get; set; } = "Registrado";
    public string? ErrorMessage { get; set; }
    public string Position { get; set; } = "";
    public string OrderType { get; set; } = "";
    public string TokenId { get; set; } = "";
    public string ConditionId { get; set; } = "";
    public decimal RequestedSize { get; set; }
    public decimal RequestedPrice { get; set; }
    public decimal MaxSlippagePercent { get; set; }
    public DateTime TradeTimestamp { get; set; }
    public DateTime CopiedAt { get; set; }

    public string WalletLabel => string.IsNullOrWhiteSpace(WalletAddress)
        ? "Carteira"
        : $"{WalletAddress[..Math.Min(6, WalletAddress.Length)]}…{WalletAddress[^Math.Min(4, WalletAddress.Length)..]}";

    public string TradeLabel => $"{Side} · {Outcome}";
    public string CopyDetailsLabel => $"{CopyMode} · {Position} · {OrderType} · {RequestedSize:N2} shares @ {RequestedPrice:P0}"
                                      + (MaxSlippagePercent > 0m ? $" · slippage {MaxSlippagePercent:N2}%" : "")
                                      + (!string.IsNullOrWhiteSpace(ClobOrderId) ? $" · ID: {ClobOrderId[..Math.Min(10, ClobOrderId.Length)]}…" : "");

    public string ExecutionBadgeLabel => IsLiveOrder
        ? (ExecutionStatus == "Filled" || ExecutionStatus == "Matched" ? "REAL · EXECUTADA" : $"REAL · {ExecutionStatus.ToUpperInvariant()}")
        : "SIMULAÇÃO LOCAL";

    public string CopiedAtLabel => CopiedAt.ToLocalTime().ToString("dd/MM HH:mm");

    public static CopyTradeRecord FromTrade(WalletTrade trade, string key, CopyTradeDraft? draft = null) => new()
    {
        Key = key,
        WalletAddress = trade.WalletAddress,
        MarketTitle = trade.MarketTitle,
        Side = trade.SideLabel,
        Outcome = trade.OutcomePriceLabel,
        AmountLabel = draft is null
            ? trade.AmountLabel
            : $"${(draft.Size * draft.Price):N2}",
        CopyMode = draft?.Mode ?? "Automatico",
        ExecutionTarget = draft?.ExecutionTarget ?? "Real",
        IsLiveOrder = (draft?.ExecutionTarget ?? "Real") == "Real",
        ExecutionStatus = (draft?.ExecutionTarget ?? "Real") == "Real" ? "Enviando…" : "Simulado",
        Position = draft?.Position ?? trade.Outcome,
        RequestedSize = draft?.Size ?? trade.Size,
        RequestedPrice = draft?.Price ?? trade.Price,
        OrderType = draft?.OrderType ?? "FOK",
        TokenId = draft?.TokenId ?? trade.TokenId,
        ConditionId = draft?.ConditionId ?? trade.ConditionId,
        MaxSlippagePercent = draft?.MaxSlippagePercent ?? 0m,
        TradeTimestamp = trade.Timestamp,
        CopiedAt = DateTime.UtcNow
    };
}
