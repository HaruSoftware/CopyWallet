using System.Globalization;

namespace WalletCopy.Models;

public sealed class WalletTrade
{
    public string MarketTitle { get; init; } = "Mercado";
    public string Outcome { get; init; } = "—";
    public string Side { get; init; } = "TRADE";
    public string WalletAddress { get; init; } = "";
    public string WalletLabel => string.IsNullOrWhiteSpace(WalletAddress)
        ? "Carteira"
        : $"{WalletAddress[..Math.Min(6, WalletAddress.Length)]}…{WalletAddress[^Math.Min(4, WalletAddress.Length)..]}";
    public string MarketImageUrl { get; init; } = "";
    public string MarketSlug { get; init; } = "";
    public string TokenId { get; init; } = "";
    public string ConditionId { get; init; } = "";
    public string TransactionHash { get; init; } = "";
    public decimal Price { get; init; }
    public decimal Size { get; init; }
    public decimal UsdcSize { get; init; }
    public bool IsTaker { get; init; }
    public string ExecutionRoleLabel => IsTaker ? "TAKER" : "MAKER";
    public bool FeeScheduleAvailable { get; init; }
    public bool FeesEnabled { get; init; }
    public decimal TakerFeeRate { get; init; }
    public decimal FeeExponent { get; init; } = 1m;
    public DateTime Timestamp { get; init; }
    public bool IsCopied { get; set; }
    public string CopyActionLabel => IsCopied ? "Copiado" : "Copiar trade";
    public string PriceLabel => $"{Price:P0}";
    public string SideLabel => Side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? "Buy" : "Sell";
    public string OutcomePriceLabel => $"{Outcome} {Price * 100m:N0}¢";
    public string SharesLabel => $"{Size.ToString("N1", CultureInfo.GetCultureInfo("en-US"))} shares";
    public decimal FeeUsdcSize
    {
        get
        {
            if (!IsTaker || !FeeScheduleAvailable || !FeesEnabled || TakerFeeRate <= 0m
                || FeeExponent < 0m || Price is <= 0m or >= 1m)
            {
                return 0m;
            }

            var priceFactor = Price * (1m - Price);
            var fee = Size * TakerFeeRate
                      * (decimal)Math.Pow((double)priceFactor, (double)FeeExponent);
            return decimal.Round(fee, 5, MidpointRounding.AwayFromZero);
        }
    }
    public decimal NetUsdcSize => !IsTaker || !FeeScheduleAvailable
        ? UsdcSize
        : IsBuy ? UsdcSize + FeeUsdcSize : Math.Max(0m, UsdcSize - FeeUsdcSize);
    public string AmountLabel => $"${UsdcSize.ToString("N2", CultureInfo.GetCultureInfo("en-US"))}";
    public string NetAmountLabel
    {
        get
        {
            if (IsTaker && !FeeScheduleAvailable)
            {
                return "Valor líquido indisponível · taxa não carregada";
            }

            var netLabel = $"${NetUsdcSize.ToString("N2", CultureInfo.GetCultureInfo("en-US"))}";
            if (!IsTaker || !FeesEnabled || FeeUsdcSize <= 0m)
            {
                return $"Líquido {netLabel}";
            }

            var feeLabel = $"taxa ${FeeUsdcSize.ToString("N2", CultureInfo.GetCultureInfo("en-US"))}";
            return IsBuy
                ? $"Total c/ taxa {netLabel} · {feeLabel}"
                : $"Após taxa {netLabel} · {feeLabel}";
        }
    }
    public string TimeLabel => Timestamp == default ? "Agora" : Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string TimestampLabel => Timestamp == default ? "Horário indisponível" : Timestamp.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string TimeAgoLabel
    {
        get
        {
            var elapsed = DateTime.UtcNow - Timestamp.ToUniversalTime();
            if (elapsed < TimeSpan.Zero || elapsed < TimeSpan.FromMinutes(1))
            {
                return "now";
            }

            if (elapsed < TimeSpan.FromHours(1))
            {
                return $"{(int)elapsed.TotalMinutes}m ago";
            }

            if (elapsed < TimeSpan.FromDays(1))
            {
                return $"{(int)elapsed.TotalHours}h ago";
            }

            return $"{(int)elapsed.TotalDays}d ago";
        }
    }
    public bool IsBuy => Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
}
