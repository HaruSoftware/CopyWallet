namespace WalletCopy.Models;

public sealed class CopyTradeDraft
{
    public string Mode { get; init; } = "Automatico";
    public string Position { get; init; } = "";
    public decimal Size { get; init; }
    public decimal Price { get; init; }
}
