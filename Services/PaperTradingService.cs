using System.IO;
using WalletCopy.Models;

namespace WalletCopy.Services;

public sealed class PaperTradingService
{
    public PaperOrder Simulate(PaperAccount account, PaperOrderRequest request)
    {
        if (request.Shares <= 0m || request.Price <= 0m || request.Price >= 1m)
        {
            throw new InvalidDataException("Informe shares maiores que zero e preço entre 0 e 1.");
        }

        var requestedValue = request.Shares * request.Price;
        if (requestedValue > account.MaxTradeValue)
        {
            throw new InvalidDataException($"O valor do trade excede o limite de ${account.MaxTradeValue:N2}.");
        }

        var currentExposure = account.Orders
            .Where(order => order.Status == "Filled")
            .Sum(order => order.TotalCost);
        if (currentExposure + requestedValue > account.MaxExposure)
        {
            throw new InvalidDataException($"A exposição excede o limite de ${account.MaxExposure:N2}.");
        }

        if (requestedValue > account.Balance)
        {
            throw new InvalidDataException("Saldo virtual insuficiente.");
        }

        var slippage = Math.Min(request.MaxSlippagePercent, 100m) / 100m;
        var simulatedPrice = request.Side.Equals("BUY", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(0.9999m, request.Price * (1m + slippage * 0.35m))
            : Math.Max(0.0001m, request.Price * (1m - slippage * 0.35m));
        var filledShares = request.OrderType == "GTC" ? request.Shares * 0.92m : request.Shares;
        var totalCost = filledShares * simulatedPrice;
        var order = new PaperOrder
        {
            MarketId = request.MarketId,
            MarketTitle = request.MarketTitle,
            Outcome = request.Outcome,
            Side = request.Side,
            OrderType = request.OrderType,
            Shares = request.Shares,
            Price = simulatedPrice,
            FilledShares = filledShares,
            TotalCost = totalCost,
            MaxSlippagePercent = request.MaxSlippagePercent,
            IsSplit = request.IsSplit,
            Status = filledShares < request.Shares ? "Partial fill" : "Filled"
        };
        account.Balance -= totalCost;
        account.Orders.Insert(0, order);
        return order;
    }

    public PaperOrder SimulateSplit(PaperAccount account, PaperMarket market, decimal amount)
    {
        if (market.Outcomes.Count < 2)
        {
            throw new InvalidDataException("Este mercado não possui dois outcomes para simular split.");
        }

        var price = market.Outcomes.Take(2).Sum(outcome => outcome.Price);
        if (price <= 0m || price > 1.05m)
        {
            throw new InvalidDataException("Os preços disponíveis não permitem simular o split com segurança.");
        }

        var shares = amount / Math.Max(price, 0.0001m);
        return Simulate(account, new PaperOrderRequest
        {
            MarketId = market.Id,
            MarketTitle = market.Title,
            TokenId = market.Outcomes[0].TokenId,
            Outcome = "Split · " + string.Join(" + ", market.Outcomes.Take(2).Select(item => item.Name)),
            Shares = shares,
            Price = Math.Min(price, 0.9999m),
            MaxSlippagePercent = 0m,
            IsSplit = true
        });
    }
}
