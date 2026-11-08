namespace WalletCopy.Models;

public readonly record struct MarketFeeSchedule(
    bool IsAvailable,
    bool FeesEnabled,
    decimal Rate,
    decimal Exponent,
    string? ErrorMessage = null)
{
    public static MarketFeeSchedule NoFees => new(true, false, 0m, 1m);
    public static MarketFeeSchedule Unavailable(string errorMessage) => new(false, false, 0m, 1m, errorMessage);
}
