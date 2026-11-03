namespace WalletCopy.Models;

public sealed class TrackedWallet
{
    public string Address { get; init; } = "";
    public string ShortAddress => $"{Address[..6]}…{Address[^4..]}";
}
