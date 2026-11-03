namespace WalletCopy.Models;

public sealed class MarketCard
{
    public string Title { get; init; } = "Mercado";
    public string ImageUrl { get; init; } = "";
    public string VolumeLabel { get; init; } = "";
    public string Category { get; init; } = "Polymarket";
    public string MarketId { get; init; } = "";
    public string Slug { get; init; } = "";
    public decimal Volume24h { get; init; }
    public List<MarketOutcome> Outcomes { get; init; } = [];
}
