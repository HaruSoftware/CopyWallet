using System.IO;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class PaperTradingService
{
    public PaperOrder Simulate(PaperAccount account, PaperOrderRequest request, MarketOrderBook orderBook)
    {
        ValidateRequest(request);

        var requestedValue = request.Shares * request.Price;
        if (requestedValue > account.MaxTradeValue)
        {
            throw new InvalidDataException($"O valor da ordem excede o limite de ${account.MaxTradeValue:N2}.");
        }

        var isBuy = request.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        var position = account.Positions.FirstOrDefault(item =>
            item.Status == "Open"
            && item.ConditionId.Equals(request.ConditionId, StringComparison.OrdinalIgnoreCase)
            && item.TokenId.Equals(request.TokenId, StringComparison.Ordinal));
        if (!isBuy && position is null)
        {
            throw new InvalidDataException("Não há posição aberta desse outcome para vender.");
        }

        var openSellShares = account.Orders
            .Where(order => (order.Status is "Open" or "Partial fill")
                            && order.Side.Equals("SELL", StringComparison.OrdinalIgnoreCase)
                            && order.TokenId.Equals(request.TokenId, StringComparison.Ordinal))
            .Sum(order => order.RemainingShares);
        if (!isBuy && position!.Shares - openSellShares < request.Shares)
        {
            throw new InvalidDataException("As shares disponíveis são insuficientes para esta ordem.");
        }

        var fills = GetAvailableFills(
            orderBook,
            request.Side,
            request.Price,
            request.Shares);
        var filledShares = fills.Sum(fill => fill.Shares);
        var remainingShares = request.Shares - filledShares;
        var isGoodUntilCancelled = request.OrderType.Equals("GTC", StringComparison.OrdinalIgnoreCase);
        if (isGoodUntilCancelled && account.Orders.Any(order =>
                (order.Status is "Open" or "Partial fill")
                && order.TokenId.Equals(request.TokenId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Já existe uma ordem GTC aberta para este outcome.");
        }

        if (request.OrderType.Equals("FOK", StringComparison.OrdinalIgnoreCase)
            && remainingShares > 0m)
        {
            throw new InvalidDataException("Liquidez insuficiente para preencher toda a ordem. Tente FAK ou ajuste o preço limite.");
        }

        if (request.OrderType.Equals("FAK", StringComparison.OrdinalIgnoreCase)
            && filledShares == 0m)
        {
            throw new InvalidDataException("Nenhuma quantidade disponível dentro do preço limite.");
        }

        if (isBuy)
        {
            var currentExposure = account.Positions
                .Where(item => item.Status == "Open")
                .Sum(item => item.CostBasis)
                + account.Orders
                    .Where(order => (order.Status is "Open" or "Partial fill")
                                    && order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                    .Sum(order => order.ReservedCash);
            if (currentExposure + requestedValue > account.MaxExposure)
            {
                throw new InvalidDataException($"A exposição excede o limite de ${account.MaxExposure:N2}.");
            }

            var fillCost = fills.Sum(fill => fill.Shares * fill.Price);
            var reservedCash = isGoodUntilCancelled ? remainingShares * request.Price : 0m;
            if (fillCost + reservedCash > account.Balance)
            {
                throw new InvalidDataException("Saldo virtual insuficiente para preencher ou reservar esta ordem.");
            }
        }

        var totalValue = fills.Sum(fill => fill.Shares * fill.Price);
        var averageFillPrice = filledShares > 0m ? totalValue / filledShares : request.Price;
        var order = new PaperOrder
        {
            MarketId = request.MarketId,
            ConditionId = request.ConditionId,
            TokenId = request.TokenId,
            MarketTitle = request.MarketTitle,
            Outcome = request.Outcome,
            Side = request.Side.ToUpperInvariant(),
            OrderType = request.OrderType.ToUpperInvariant(),
            Shares = request.Shares,
            Price = averageFillPrice,
            LimitPrice = request.Price,
            FilledShares = filledShares,
            RemainingShares = isGoodUntilCancelled ? remainingShares : 0m,
            TotalCost = totalValue,
            ReservedCash = isBuy && isGoodUntilCancelled ? remainingShares * request.Price : 0m,
            MaxSlippagePercent = request.MaxSlippagePercent,
            Status = remainingShares > 0m
                ? filledShares > 0m ? "Partial fill" : "Open"
                : "Filled"
        };

        if (isBuy)
        {
            account.Balance -= totalValue + order.ReservedCash;
            if (filledShares > 0m)
            {
                AddPosition(account, request, filledShares, averageFillPrice);
            }
        }
        else
        {
            account.Balance += totalValue;
            if (filledShares > 0m)
            {
                ClosePosition(account, position!, filledShares, averageFillPrice);
            }
        }

        account.Orders.Insert(0, order);
        return order;
    }

    public bool CancelOrder(PaperAccount account, string orderId)
    {
        var order = account.Orders.FirstOrDefault(item =>
            item.Id == orderId && (item.Status is "Open" or "Partial fill"));
        if (order is null)
        {
            return false;
        }

        account.Balance += order.ReservedCash;
        order.ReservedCash = 0m;
        order.RemainingShares = 0m;
        order.Status = "Cancelled";
        return true;
    }

    public bool MatchOpenOrder(PaperAccount account, PaperOrder order, MarketOrderBook orderBook)
    {
        if (order.Status is not ("Open" or "Partial fill") || order.RemainingShares <= 0m)
        {
            return false;
        }

        var fills = GetAvailableFills(orderBook, order.Side, order.LimitPrice, order.RemainingShares);
        var filledShares = fills.Sum(fill => fill.Shares);
        if (filledShares == 0m)
        {
            return false;
        }

        var totalValue = fills.Sum(fill => fill.Shares * fill.Price);
        var fillPrice = totalValue / filledShares;
        var previousRemaining = order.RemainingShares;
        order.RemainingShares -= filledShares;
        order.FilledShares += filledShares;
        order.TotalCost += totalValue;
        order.Price = order.TotalCost / order.FilledShares;
        if (order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
        {
            var oldReservation = order.ReservedCash;
            order.ReservedCash = order.RemainingShares * order.LimitPrice;
            account.Balance += oldReservation - totalValue - order.ReservedCash;
            AddPosition(account, new PaperOrderRequest
            {
                ConditionId = order.ConditionId,
                MarketTitle = order.MarketTitle,
                TokenId = order.TokenId,
                Outcome = order.Outcome
            }, filledShares, fillPrice);
        }
        else
        {
            var position = account.Positions.FirstOrDefault(item =>
                item.Status == "Open"
                && item.ConditionId.Equals(order.ConditionId, StringComparison.OrdinalIgnoreCase)
                && item.TokenId.Equals(order.TokenId, StringComparison.Ordinal));
            if (position is null || position.Shares < filledShares)
            {
                throw new InvalidDataException("A posição da ordem GTC não está mais disponível para venda.");
            }

            account.Balance += totalValue;
            ClosePosition(account, position, filledShares, fillPrice);
        }

        order.Status = order.RemainingShares == 0m
            ? "Filled"
            : order.FilledShares > 0m ? "Partial fill" : "Open";
        if (order.Status == "Filled")
        {
            order.ReservedCash = 0m;
        }

        return order.RemainingShares < previousRemaining;
    }

    public void UpdatePositionPrices(PaperAccount account, IEnumerable<MarketCard> markets)
    {
        var outcomesByToken = markets
            .SelectMany(market => market.Outcomes)
            .Where(outcome => !string.IsNullOrWhiteSpace(outcome.TokenId))
            .GroupBy(outcome => outcome.TokenId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var position in account.Positions.Where(item => item.Status == "Open"))
        {
            if (outcomesByToken.TryGetValue(position.TokenId, out var outcome))
            {
                position.CurrentPrice = outcome.Price;
            }
        }
    }

    public (int SettledPositions, int CancelledOrders) ResolvePositions(
        PaperAccount account,
        IEnumerable<MarketCard> resolvedMarkets)
    {
        var resolvedByCondition = resolvedMarkets
            .Where(market => market.IsClosed && !string.IsNullOrWhiteSpace(market.ConditionId))
            .GroupBy(market => market.ConditionId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var cancelledOrders = 0;
        foreach (var order in account.Orders.Where(order =>
                     resolvedByCondition.ContainsKey(order.ConditionId)
                     && (order.Status is "Open" or "Partial fill")))
        {
            account.Balance += order.ReservedCash;
            order.ReservedCash = 0m;
            order.RemainingShares = 0m;
            order.Status = "Cancelled";
            cancelledOrders++;
        }

        var settledCount = 0;
        foreach (var position in account.Positions.Where(item => item.Status == "Open"))
        {
            if (!resolvedByCondition.TryGetValue(position.ConditionId, out var market))
            {
                continue;
            }

            var outcome = market.Outcomes.FirstOrDefault(item =>
                item.TokenId.Equals(position.TokenId, StringComparison.Ordinal));
            if (outcome is null || (outcome.Price < 0.99m && outcome.Price > 0.01m))
            {
                continue;
            }

            var payout = position.Shares * outcome.Price;
            var realizedPnl = payout - position.CostBasis;
            account.Balance += payout;
            account.RealizedPnl += realizedPnl;
            position.RealizedPnl += realizedPnl;
            position.CurrentPrice = outcome.Price;
            position.Status = "Resolved";
            position.ClosedAt = market.EndDate ?? DateTime.UtcNow;
            settledCount++;
        }

        return (settledCount, cancelledOrders);
    }

    public PaperOrder SimulateSplit(PaperAccount account, PaperMarket market, decimal amount)
    {
        if (market.Outcomes.Count < 2)
        {
            throw new InvalidDataException("Este mercado não possui dois outcomes para simular split.");
        }

        if (amount <= 0m || amount > account.MaxTradeValue)
        {
            throw new InvalidDataException($"Informe um valor entre $0,01 e ${account.MaxTradeValue:N2} para o split.");
        }

        if (amount > account.Balance)
        {
            throw new InvalidDataException("Saldo virtual insuficiente.");
        }

        var currentExposure = account.Positions
            .Where(item => item.Status == "Open")
            .Sum(item => item.CostBasis)
            + account.Orders
                .Where(order => (order.Status is "Open" or "Partial fill")
                                && order.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                .Sum(order => order.ReservedCash);
        if (currentExposure + amount > account.MaxExposure)
        {
            throw new InvalidDataException($"A exposição excede o limite de ${account.MaxExposure:N2}.");
        }

        var firstOutcome = market.Outcomes[0];
        var secondOutcome = market.Outcomes[1];
        if (string.IsNullOrWhiteSpace(market.ConditionId)
            || string.IsNullOrWhiteSpace(firstOutcome.TokenId)
            || string.IsNullOrWhiteSpace(secondOutcome.TokenId))
        {
            throw new InvalidDataException("Este mercado não possui identificadores públicos suficientes para simular split.");
        }

        var shares = amount;
        AddPosition(account, market, firstOutcome, shares, 0.5m);
        AddPosition(account, market, secondOutcome, shares, 0.5m);
        account.Balance -= amount;
        var order = new PaperOrder
        {
            MarketId = market.Id,
            ConditionId = market.ConditionId,
            MarketTitle = market.Title,
            Outcome = $"Split · {firstOutcome.Name} + {secondOutcome.Name}",
            Shares = shares,
            FilledShares = shares,
            Price = 0.5m,
            LimitPrice = 0.5m,
            TotalCost = amount,
            IsSplit = true,
            Status = "Filled"
        };
        account.Orders.Insert(0, order);
        return order;
    }

    private static void ValidateRequest(PaperOrderRequest request)
    {
        if (request.Shares <= 0m || request.Price <= 0m || request.Price >= 1m)
        {
            throw new InvalidDataException("Informe shares maiores que zero e preço limite entre 0 e 1.");
        }

        if (string.IsNullOrWhiteSpace(request.ConditionId) || string.IsNullOrWhiteSpace(request.TokenId))
        {
            throw new InvalidDataException("O mercado selecionado não tem identificadores públicos para simulação.");
        }

        if (request.Side is not ("BUY" or "SELL")
            || request.OrderType is not ("FOK" or "FAK" or "GTC"))
        {
            throw new InvalidDataException("Selecione um lado e tipo de ordem válidos.");
        }
    }

    private static List<Fill> GetAvailableFills(
        MarketOrderBook orderBook,
        string side,
        decimal limitPrice,
        decimal requestedShares)
    {
        var isBuy = side.Equals("BUY", StringComparison.OrdinalIgnoreCase);
        var levels = (isBuy ? orderBook.Asks : orderBook.Bids)
            .Where(level => level.Shares > 0m
                            && (isBuy ? level.Price <= limitPrice : level.Price >= limitPrice))
            .OrderBy(level => isBuy ? level.Price : -level.Price);
        var remaining = requestedShares;
        var fills = new List<Fill>();
        foreach (var level in levels)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var shares = Math.Min(remaining, level.Shares);
            fills.Add(new Fill(shares, level.Price));
            remaining -= shares;
        }

        return fills;
    }

    private static void AddPosition(
        PaperAccount account,
        PaperOrderRequest request,
        decimal shares,
        decimal fillPrice)
    {
        var position = account.Positions.FirstOrDefault(item =>
            item.Status == "Open"
            && item.ConditionId.Equals(request.ConditionId, StringComparison.OrdinalIgnoreCase)
            && item.TokenId.Equals(request.TokenId, StringComparison.Ordinal));
        if (position is null)
        {
            account.Positions.Insert(0, new PaperPosition
            {
                ConditionId = request.ConditionId,
                MarketTitle = request.MarketTitle,
                TokenId = request.TokenId,
                Outcome = request.Outcome,
                Shares = shares,
                AveragePrice = fillPrice,
                CurrentPrice = fillPrice
            });
            return;
        }

        position.AveragePrice = ((position.Shares * position.AveragePrice) + (shares * fillPrice))
                                / (position.Shares + shares);
        position.Shares += shares;
    }

    private static void AddPosition(
        PaperAccount account,
        PaperMarket market,
        MarketOutcome outcome,
        decimal shares,
        decimal fillPrice)
    {
        AddPosition(account, new PaperOrderRequest
        {
            MarketId = market.Id,
            ConditionId = market.ConditionId,
            MarketTitle = market.Title,
            TokenId = outcome.TokenId,
            Outcome = outcome.Name,
            Side = "BUY",
            Price = fillPrice,
            Shares = shares
        }, shares, fillPrice);
    }

    private static void ClosePosition(
        PaperAccount account,
        PaperPosition position,
        decimal shares,
        decimal fillPrice)
    {
        position.Shares -= shares;
        var realizedPnl = (fillPrice - position.AveragePrice) * shares;
        position.RealizedPnl += realizedPnl;
        account.RealizedPnl += realizedPnl;
        if (position.Shares <= 0m)
        {
            position.Shares = 0m;
            position.Status = "Closed";
            position.ClosedAt = DateTime.UtcNow;
            position.CurrentPrice = fillPrice;
        }
    }

    private readonly record struct Fill(decimal Shares, decimal Price);
}
