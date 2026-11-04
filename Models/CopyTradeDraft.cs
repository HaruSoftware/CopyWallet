namespace WalletCopy.Models;

public sealed class CopyTradeDraft
{
    public string Mode { get; init; } = "Automatico";
    public string ExecutionTarget { get; init; } = "Real"; // "Real" (Polymarket CLOB) ou "Paper" (Simulado)
    public string Position { get; init; } = "";
    public string Side { get; init; } = "BUY";
    public string OrderType { get; init; } = "FOK";
    public string TokenId { get; init; } = "";
    public string ConditionId { get; init; } = "";
    public decimal Size { get; init; }
    public decimal Price { get; init; }
    public decimal MaxSlippagePercent { get; init; }
    public bool IsSplit { get; init; }
}
