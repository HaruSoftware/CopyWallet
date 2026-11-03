namespace WalletCopy.Models;

public sealed class WalletPosition
{
    public string MarketTitle { get; init; } = "Mercado";
    public string Outcome { get; init; } = "—";
    public string ImageUrl { get; init; } = "";
    public decimal CurrentSize { get; init; }
    public decimal AveragePrice { get; init; }
    public decimal CurrentValue { get; init; }
    public decimal TotalPnl { get; init; }
    public decimal PercentPnl { get; init; }
    public bool IsPnlPositive => TotalPnl >= 0;
    public string SharesLabel => $"{CurrentSize:N2} cotas";
    public string ValueLabel => CurrentValue.ToString("C2");
    public string PnlLabel => $"{(TotalPnl >= 0 ? "+" : "")}{TotalPnl:C2}";
    public string PercentPnlLabel => $"{(PercentPnl >= 0 ? "+" : "")}{PercentPnl:N1}%";
}
