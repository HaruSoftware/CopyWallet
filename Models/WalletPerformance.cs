using System.Globalization;

namespace WalletCopy.Models;

public sealed class WalletPerformance
{
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public decimal? DailyPnl { get; init; }
    public decimal? AllTimePnl { get; init; }
    public decimal? PortfolioValue { get; init; }

    public string DailyPnlLabel => FormatPnl(DailyPnl);
    public string AllTimePnlLabel => FormatPnl(AllTimePnl);
    public string PortfolioValueLabel => PortfolioValue is decimal value
        ? $"${value.ToString("N2", UsCulture)}"
        : "—";
    private static string FormatPnl(decimal? pnl)
    {
        if (pnl is not decimal value)
        {
            return "—";
        }

        return value < 0m
            ? $"-${Math.Abs(value).ToString("N2", UsCulture)}"
            : $"{(value > 0m ? "+" : "")}${value.ToString("N2", UsCulture)}";
    }
}
