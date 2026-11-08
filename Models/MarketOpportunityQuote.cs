using System.Globalization;

namespace WalletCopy.Models;

public sealed class MarketOpportunityQuote
{
    public required MarketCard Market { get; init; }
    public required MarketOutcome FirstOutcome { get; init; }
    public required MarketOutcome SecondOutcome { get; init; }
    public MarketOrderBook? FirstBook { get; init; }
    public MarketOrderBook? SecondBook { get; init; }
    public MarketFeeSchedule? FeeSchedule { get; init; }
    public decimal? TargetAmount { get; init; }

    public string Title => Market.Title;
    public string MarketLabel => $"{Market.Asset} · {Market.TimeframeLabel}";
    public string TimeRemainingLabel => Market.TimeRemainingLabel;
    public string FirstBidLabel => FormatBid(FirstOutcome, FirstBook);
    public string SecondBidLabel => FormatBid(SecondOutcome, SecondBook);
    public string DirectionalStrengthLabel => FormatDirectionalStrength();
    public string TargetLabel => TargetAmount.HasValue
        ? $"Meta líquida: ${TargetAmount.Value.ToString("0.00", CultureInfo.CurrentCulture)}"
        : "Meta inválida";

    public bool IsOpportunity => TargetAmount.HasValue
                                 && NetProceeds.HasValue
                                 && NetProceeds.Value >= TargetAmount.Value;
    public string NetProceedsLabel => NetProceeds.HasValue
        ? $"${NetProceeds.Value.ToString("0.0000", CultureInfo.CurrentCulture)}"
        : "—";
    public string ExcessLabel => NetProceeds.HasValue
        ? $"{(NetProceeds.Value - 1m):+$0.0000;-$0.0000;$0.0000}"
        : "—";

    public string StatusLabel
    {
        get
        {
            if (FirstBook is null || SecondBook is null)
            {
                return "Aguardando book";
            }

            if (FeeSchedule is not { IsAvailable: true })
            {
                return "Taxa indisponível · líquido não estimado";
            }

            var firstEstimate = EstimateSell(FirstBook, FeeSchedule.Value);
            var secondEstimate = EstimateSell(SecondBook, FeeSchedule.Value);
            if (firstEstimate.Shares < 1m || secondEstimate.Shares < 1m)
            {
                return $"Liquidez insuficiente · {firstEstimate.Shares:N2}/{secondEstimate.Shares:N2} shares";
            }

            return IsOpportunity ? "OPORTUNIDADE · confira o book" : "Abaixo da meta";
        }
    }

    private decimal? NetProceeds
    {
        get
        {
            if (FirstBook is null
                || SecondBook is null
                || FeeSchedule is not { IsAvailable: true } feeSchedule)
            {
                return null;
            }

            var firstEstimate = EstimateSell(FirstBook, feeSchedule);
            var secondEstimate = EstimateSell(SecondBook, feeSchedule);
            return firstEstimate.Shares >= 1m && secondEstimate.Shares >= 1m
                ? firstEstimate.NetProceeds + secondEstimate.NetProceeds
                : null;
        }
    }

    private static string FormatBid(MarketOutcome outcome, MarketOrderBook? book)
    {
        var bids = book?.Bids
            .Where(level => level.Price is > 0m and <= 1m && level.Shares > 0m)
            .ToArray();
        if (bids is null || bids.Length == 0)
        {
            return $"{outcome.Name}: sem bid";
        }

        var bestBid = bids.Max(level => level.Price);
        var availableShares = bids.Sum(level => level.Shares);
        return $"{outcome.Name}: {bestBid:P1} · depth {availableShares:N2}";
    }

    private string FormatDirectionalStrength()
    {
        var firstBids = GetValidBids(FirstBook);
        var secondBids = GetValidBids(SecondBook);
        if (firstBids.Length == 0 || secondBids.Length == 0)
        {
            return "Aguardando bids dos dois lados";
        }

        var firstBestBid = firstBids.Max(level => level.Price);
        var secondBestBid = secondBids.Max(level => level.Price);
        var firstDepth = firstBids.Sum(level => level.Shares);
        var secondDepth = secondBids.Sum(level => level.Shares);
        var totalBestBid = firstBestBid + secondBestBid;
        var totalDepth = firstDepth + secondDepth;
        if (totalBestBid <= 0m || totalDepth <= 0m)
        {
            return "Aguardando bids dos dois lados";
        }

        var firstStrength = ((firstBestBid / totalBestBid) + (firstDepth / totalDepth)) / 2m;
        var secondStrength = 1m - firstStrength;
        var firstDirection = GetDirectionLabel(FirstOutcome, 1);
        var secondDirection = GetDirectionLabel(SecondOutcome, 2);
        return firstDirection == "DOWN" && secondDirection == "UP"
            ? $"UP {secondStrength:P0} · DOWN {firstStrength:P0}"
            : $"{firstDirection} {firstStrength:P0} · {secondDirection} {secondStrength:P0}";
    }

    private static OrderBookLevel[] GetValidBids(MarketOrderBook? book) =>
        book?.Bids
            .Where(level => level.Price is > 0m and <= 1m && level.Shares > 0m)
            .ToArray()
        ?? [];

    private static string GetDirectionLabel(MarketOutcome outcome, int index)
    {
        var name = outcome.Name.Trim();
        if (name.Equals("up", StringComparison.OrdinalIgnoreCase)
            || name.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            return "UP";
        }

        if (name.Equals("down", StringComparison.OrdinalIgnoreCase)
            || name.Equals("no", StringComparison.OrdinalIgnoreCase))
        {
            return "DOWN";
        }

        return $"LADO {index}";
    }

    private static (decimal Shares, decimal NetProceeds) EstimateSell(
        MarketOrderBook book,
        MarketFeeSchedule feeSchedule)
    {
        var remainingShares = 1m;
        var netProceeds = 0m;
        foreach (var level in book.Bids.OrderByDescending(level => level.Price))
        {
            if (level.Price is <= 0m or > 1m || level.Shares <= 0m)
            {
                continue;
            }

            var filledShares = Math.Min(remainingShares, level.Shares);
            var grossProceeds = filledShares * level.Price;
            var fee = feeSchedule.FeesEnabled
                      && feeSchedule.Rate > 0m
                      && feeSchedule.Exponent >= 0m
                      && level.Price < 1m
                ? filledShares
                  * feeSchedule.Rate
                  * (decimal)Math.Pow(
                      (double)(level.Price * (1m - level.Price)),
                      (double)feeSchedule.Exponent)
                : 0m;
            netProceeds += grossProceeds - decimal.Round(fee, 5, MidpointRounding.AwayFromZero);
            remainingShares -= filledShares;
            if (remainingShares <= 0m)
            {
                break;
            }
        }

        return (1m - remainingShares, netProceeds);
    }
}
