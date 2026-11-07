using System.Globalization;

namespace WalletCopy.Models;

public sealed class WalletTradeAnalysis
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");

    public string Profile { get; private init; } = "";
    public string Summary { get; private init; } = "";
    public string Evidence { get; private init; } = "";
    public string ProfitabilitySummary { get; private init; } = "";
    public string ResultLabel { get; private init; } = "";
    public string ReturnLabel { get; private init; } = "";
    public string PeriodLabel { get; private init; } = "Sem período disponível";
    public string TradeWindowLabel { get; private init; } = "Nenhum trade";
    public bool HasRealizedResult { get; private init; }
    public bool HasIncompleteFeeData { get; private init; }
    public decimal RealizedResult { get; private init; }
    public decimal MatchedInvestment { get; private init; }
    public decimal AverageResultPerMatchedSale { get; private init; }
    public decimal ReturnOnMatchedInvestment { get; private init; }
    public decimal GrossBuyAmount { get; private init; }
    public int UnmatchedSaleCount { get; private init; }
    public int TradeCount { get; private init; }
    public int MarketCount { get; private init; }
    public decimal AverageTradeValue { get; private init; }
    public decimal TakerPercentage { get; private init; }
    public string AverageTradeValueLabel => $"${AverageTradeValue.ToString("N2", EnglishCulture)}";
    public string GrossBuyAmountLabel => $"${GrossBuyAmount.ToString("N2", EnglishCulture)}";
    public string RealizedResultLabel => HasRealizedResult
        ? $"{(RealizedResult > 0m ? "+" : RealizedResult < 0m ? "-" : "")}${Math.Abs(RealizedResult).ToString("N2", EnglishCulture)}"
        : "Não estimável";
    public string MatchedInvestmentLabel => HasRealizedResult
        ? $"sobre ${MatchedInvestment.ToString("N2", EnglishCulture)} pareados"
        : "sem venda pareada na amostra";
    public string AverageResultPerMatchedSaleLabel => HasRealizedResult
        ? $"{(AverageResultPerMatchedSale > 0m ? "+" : AverageResultPerMatchedSale < 0m ? "-" : "")}${Math.Abs(AverageResultPerMatchedSale).ToString("N2", EnglishCulture)} por venda pareada"
        : "";
    public string ReturnOnMatchedInvestmentLabel => HasRealizedResult
        ? $"{(ReturnOnMatchedInvestment > 0m ? "+" : "")}{ReturnOnMatchedInvestment.ToString("N2", EnglishCulture)}%"
        : "—";
    public string TakerPercentageLabel => $"{TakerPercentage:N0}%";
    public string TradeCountLabel => TradeCount.ToString("N0", EnglishCulture);
    public string MarketCountLabel => MarketCount.ToString("N0", EnglishCulture);

    public static WalletTradeAnalysis Create(IReadOnlyList<WalletTrade> trades)
    {
        var orderedTrades = trades
            .Where(trade => trade.Timestamp != default)
            .OrderBy(trade => trade.Timestamp)
            .TakeLast(30)
            .ToArray();
        var tradeCount = orderedTrades.Length;
        var marketCount = orderedTrades
            .Select(trade => string.IsNullOrWhiteSpace(trade.MarketSlug) ? trade.MarketTitle : trade.MarketSlug)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var averageTradeValue = tradeCount == 0 ? 0m : orderedTrades.Average(trade => trade.UsdcSize);
        var takerPercentage = tradeCount == 0
            ? 0m
            : orderedTrades.Count(trade => trade.IsTaker) * 100m / tradeCount;
        var medianGapMinutes = GetMedianGapMinutes(orderedTrades);
        var sellPercentage = tradeCount == 0
            ? 0m
            : orderedTrades.Count(trade => !trade.IsBuy) * 100m / tradeCount;
        var grossBuyAmount = orderedTrades
            .Where(trade => trade.IsBuy)
            .Sum(trade => trade.UsdcSize);
        var realizedResult = CalculateRealizedResult(orderedTrades);
        var hasRealizedResult = realizedResult.MatchedInvestment > 0m;
        var returnOnMatchedInvestment = hasRealizedResult
            ? realizedResult.Profit / realizedResult.MatchedInvestment * 100m
            : 0m;

        var profile = "Perfil misto / inconclusivo";
        var summary = "Os trades recentes não mostram um padrão suficientemente consistente.";
        var evidence = tradeCount < 4
            ? "Poucas execuções recentes para estimar um estilo. A análise usa somente até 30 trades públicos."
            : $"Foram observados {marketCount} mercados e uma média de ${averageTradeValue.ToString("N2", EnglishCulture)} por execução.";

        if (tradeCount >= 10 && medianGapMinutes <= 1.5m)
        {
            profile = "Possíveis sinais de automação";
            summary = "A frequência e o espaçamento curto entre execuções são compatíveis com atividade automatizada.";
            evidence = $"Intervalo mediano de {FormatInterval(medianGapMinutes)} entre trades; {takerPercentage:N0}% foram executados como taker.";
        }
        else if (tradeCount >= 8 && medianGapMinutes <= 30m && sellPercentage >= 20m)
        {
            profile = "Giro rápido · possível foco em margem";
            summary = "A frequência, a alternância entre compras e vendas e o intervalo curto sugerem operações de giro.";
            evidence = $"Intervalo mediano de {FormatInterval(medianGapMinutes)}; {sellPercentage:N0}% das execuções foram vendas. Isso não permite confirmar margem ou lucro.";
        }
        else if (tradeCount >= 5
                 && (marketCount <= 2 || averageTradeValue >= 250m))
        {
            profile = "Trader de convicção · perfil raiz";
            summary = "A concentração em poucos mercados ou o valor médio elevado sugerem posicionamentos mais direcionais.";
            evidence = $"{marketCount} mercados distintos e valor médio de ${averageTradeValue.ToString("N2", EnglishCulture)} por execução.";
        }
        else if (tradeCount >= 8 && marketCount >= Math.Ceiling(tradeCount * 0.6m))
        {
            profile = "Trader diversificado";
            summary = "As execuções recentes estão distribuídas entre vários mercados, sem concentração dominante.";
            evidence = $"{marketCount} mercados distintos em {tradeCount} execuções recentes.";
        }

        var firstTrade = orderedTrades.FirstOrDefault();
        var lastTrade = orderedTrades.LastOrDefault();
        var periodLabel = firstTrade is null || lastTrade is null
            ? "Sem período disponível"
            : $"{firstTrade.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm} → {lastTrade.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm} ({FormatDuration(lastTrade.Timestamp - firstTrade.Timestamp)})";
        var profitabilitySummary = !hasRealizedResult
            ? "Não há compras e vendas suficientes do mesmo resultado dentro desta amostra para estimar lucro realizado. Vendas de posições compradas antes da amostra não entram no cálculo."
            : realizedResult.HasIncompleteFeeData
                ? "Resultado FIFO estimado nas vendas pareadas. Taxas indisponíveis foram tratadas como zero; o resultado líquido pode ser diferente. Vendas sem compra correspondente na amostra foram excluídas."
                : "Resultado FIFO estimado nas vendas pareadas dentro desta amostra, usando taxas de mercado disponíveis. Vendas sem compra correspondente na amostra foram excluídas.";
        var averageResultPerMatchedSale = realizedResult.MatchedSaleCount == 0
            ? 0m
            : realizedResult.Profit / realizedResult.MatchedSaleCount;
        var profitabilityLabel = !hasRealizedResult
            ? "Sem resultado estimável"
            : realizedResult.Profit > 0m
                ? "Resultado positivo"
                : realizedResult.Profit < 0m
                    ? "Resultado negativo"
                    : "Resultado no zero";
        var windowLabel = tradeCount == 0
            ? "Nenhum trade"
            : $"Últimos {tradeCount} trades";

        return new WalletTradeAnalysis
        {
            Profile = profile,
            Summary = summary,
            Evidence = evidence,
            ProfitabilitySummary = profitabilitySummary,
            ResultLabel = profitabilityLabel,
            ReturnLabel = hasRealizedResult
                ? "Resultado ÷ custo de compra pareado"
                : "Sem custo de compra pareado",
            PeriodLabel = periodLabel,
            TradeWindowLabel = windowLabel,
            HasRealizedResult = hasRealizedResult,
            HasIncompleteFeeData = realizedResult.HasIncompleteFeeData,
            RealizedResult = realizedResult.Profit,
            MatchedInvestment = realizedResult.MatchedInvestment,
            AverageResultPerMatchedSale = averageResultPerMatchedSale,
            ReturnOnMatchedInvestment = returnOnMatchedInvestment,
            GrossBuyAmount = grossBuyAmount,
            UnmatchedSaleCount = realizedResult.UnmatchedSaleCount,
            TradeCount = tradeCount,
            MarketCount = marketCount,
            AverageTradeValue = averageTradeValue,
            TakerPercentage = takerPercentage
        };
    }

    private static RealizedResultData CalculateRealizedResult(IReadOnlyList<WalletTrade> orderedTrades)
    {
        var openLotsByOutcome = new Dictionary<string, Queue<BuyLot>>(StringComparer.OrdinalIgnoreCase);
        var realizedProfit = 0m;
        var matchedInvestment = 0m;
        var matchedSaleCount = 0;
        var unmatchedSaleCount = 0;
        var hasIncompleteFeeData = false;

        foreach (var trade in orderedTrades)
        {
            if (trade.Size <= 0m || trade.UsdcSize < 0m)
            {
                continue;
            }

            var outcomeKey = GetOutcomeKey(trade);
            if (outcomeKey is null)
            {
                continue;
            }

            if (trade.IsBuy)
            {
                if (!openLotsByOutcome.TryGetValue(outcomeKey, out var lots))
                {
                    lots = new Queue<BuyLot>();
                    openLotsByOutcome.Add(outcomeKey, lots);
                }

                lots.Enqueue(new BuyLot(
                    trade.Size,
                    trade.NetUsdcSize / trade.Size,
                    !trade.IsTaker || trade.FeeScheduleAvailable));
                continue;
            }

            if (!openLotsByOutcome.TryGetValue(outcomeKey, out var openLots))
            {
                unmatchedSaleCount++;
                continue;
            }

            var remainingSaleSize = trade.Size;
            var salePricePerShare = trade.NetUsdcSize / trade.Size;
            var saleWasMatched = false;
            while (remainingSaleSize > 0m && openLots.TryPeek(out var lot))
            {
                var matchedSize = Math.Min(remainingSaleSize, lot.RemainingSize);
                realizedProfit += (salePricePerShare - lot.CostPerShare) * matchedSize;
                matchedInvestment += lot.CostPerShare * matchedSize;
                remainingSaleSize -= matchedSize;
                lot.RemainingSize -= matchedSize;
                hasIncompleteFeeData |= !lot.FeeDataAvailable
                                        || (trade.IsTaker && !trade.FeeScheduleAvailable);
                saleWasMatched = true;

                if (lot.RemainingSize <= 0m)
                {
                    openLots.Dequeue();
                }
            }

            if (saleWasMatched)
            {
                matchedSaleCount++;
            }
            if (remainingSaleSize > 0m)
            {
                unmatchedSaleCount++;
            }
        }

        return new RealizedResultData(
            realizedProfit,
            matchedInvestment,
            matchedSaleCount,
            unmatchedSaleCount,
            hasIncompleteFeeData);
    }

    private static string? GetOutcomeKey(WalletTrade trade)
    {
        if (!string.IsNullOrWhiteSpace(trade.TokenId))
        {
            return $"token:{trade.TokenId}";
        }

        if (string.IsNullOrWhiteSpace(trade.ConditionId) || string.IsNullOrWhiteSpace(trade.Outcome))
        {
            return null;
        }

        return $"condition:{trade.ConditionId}|{trade.Outcome}";
    }

    private static decimal GetMedianGapMinutes(IReadOnlyList<WalletTrade> orderedTrades)
    {
        if (orderedTrades.Count < 2)
        {
            return decimal.MaxValue;
        }

        var gaps = new decimal[orderedTrades.Count - 1];
        for (var index = 1; index < orderedTrades.Count; index++)
        {
            gaps[index - 1] = Math.Max(
                0m,
                (decimal)(orderedTrades[index].Timestamp - orderedTrades[index - 1].Timestamp).TotalMinutes);
        }

        Array.Sort(gaps);
        var middle = gaps.Length / 2;
        return gaps.Length % 2 == 0
            ? (gaps[middle - 1] + gaps[middle]) / 2m
            : gaps[middle];
    }

    private static string FormatInterval(decimal minutes)
    {
        if (minutes < 1m)
        {
            return $"{minutes * 60m:N0} s";
        }

        return minutes < 60m
            ? $"{minutes:N0} min"
            : $"{minutes / 60m:N1} h";
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        var parts = new List<string>(3);
        if (duration.Days > 0)
        {
            parts.Add($"{duration.Days} d");
        }

        if (duration.Hours > 0)
        {
            parts.Add($"{duration.Hours} h");
        }

        if (duration.Minutes > 0 || parts.Count == 0)
        {
            parts.Add($"{duration.Minutes} min");
        }
        else if (duration.Seconds > 0)
        {
            parts.Add($"{duration.Seconds} s");
        }

        return string.Join(" ", parts);
    }

    private sealed class BuyLot(decimal remainingSize, decimal costPerShare, bool feeDataAvailable)
    {
        public decimal RemainingSize { get; set; } = remainingSize;
        public decimal CostPerShare { get; } = costPerShare;
        public bool FeeDataAvailable { get; } = feeDataAvailable;
    }

    private sealed record RealizedResultData(
        decimal Profit,
        decimal MatchedInvestment,
        int MatchedSaleCount,
        int UnmatchedSaleCount,
        bool HasIncompleteFeeData);
}
