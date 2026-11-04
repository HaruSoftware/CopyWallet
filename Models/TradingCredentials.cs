namespace WalletCopy.Models;

public enum PolymarketWalletType
{
    Eoa = 0,
    PolymarketProxy = 1,
    GnosisSafe = 2
}

public sealed class TradingCredentials
{
    public string SignerPrivateKey { get; set; } = "";
    public string SignerAddress { get; set; } = "";
    public string FunderAddress { get; set; } = "";
    public PolymarketWalletType WalletType { get; set; } = PolymarketWalletType.PolymarketProxy;
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string ApiPassphrase { get; set; } = "";
    public DateTime? ConfiguredAt { get; set; }

    public bool HasPrivateKey => !string.IsNullOrWhiteSpace(SignerPrivateKey);
    public bool HasApiCredentials => !string.IsNullOrWhiteSpace(ApiKey)
                                     && !string.IsNullOrWhiteSpace(ApiSecret)
                                     && !string.IsNullOrWhiteSpace(ApiPassphrase);
    public bool IsConfigured => HasPrivateKey && HasApiCredentials;

    public string EffectiveFunderAddress => string.IsNullOrWhiteSpace(FunderAddress)
        ? SignerAddress
        : FunderAddress;
}

