namespace WalletCopy.Models;

public sealed class MarketCard
{
    public string Title { get; init; } = "Mercado";
    public string EventTitle { get; init; } = "";
    public string ImageUrl { get; init; } = "";
    public string VolumeLabel { get; init; } = "";
    public string Category { get; init; } = "Polymarket";
    public string MarketId { get; init; } = "";
    public string ConditionId { get; init; } = "";
    public string Slug { get; init; } = "";
    public decimal Volume24h { get; init; }
    public DateTime? EndDate { get; init; }
    public bool IsActive { get; init; }
    public bool IsClosed { get; init; }
    public string TimeframeLabel { get; init; } = "";
    public List<MarketOutcome> Outcomes { get; init; } = [];
}
