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
    public string Position { get; set; } = "";
    public decimal RequestedSize { get; set; }
    public decimal RequestedPrice { get; set; }
    public DateTime TradeTimestamp { get; set; }
    public DateTime CopiedAt { get; set; }

    public string WalletLabel => string.IsNullOrWhiteSpace(WalletAddress)
        ? "Carteira"
        : $"{WalletAddress[..Math.Min(6, WalletAddress.Length)]}…{WalletAddress[^Math.Min(4, WalletAddress.Length)..]}";

    public string TradeLabel => $"{Side} · {Outcome}";
    public string CopyDetailsLabel => $"{CopyMode} · {Position} · {RequestedSize:N2} shares @ {RequestedPrice:P0}";
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
        Position = draft?.Position ?? trade.Outcome,
        RequestedSize = draft?.Size ?? trade.Size,
        RequestedPrice = draft?.Price ?? trade.Price,
        TradeTimestamp = trade.Timestamp,
        CopiedAt = DateTime.UtcNow
    };
}
