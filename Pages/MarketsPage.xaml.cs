using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class MarketsPage : UserControl
{
    private IReadOnlyList<MarketCard>? _marketSnapshot;

    public event EventHandler<MarketCard>? MarketSelected;

    public MarketsPage()
    {
        InitializeComponent();
        MarketList.ItemsSource = new ObservableCollection<MarketCard>();
    }

    public void SetMarkets(IReadOnlyList<MarketCard> markets)
    {
        if (ReferenceEquals(_marketSnapshot, markets))
        {
            return;
        }

        var featuredMarkets = markets
            .Where(market => string.IsNullOrEmpty(market.TimeframeLabel))
            .OrderByDescending(market => market.Volume24h)
            .Take(8)
            .ToArray();
        var target = (ObservableCollection<MarketCard>)MarketList.ItemsSource;
        if (target.Count == featuredMarkets.Length
            && target.Select(market => market.ConditionId)
                .SequenceEqual(
                    featuredMarkets.Select(market => market.ConditionId),
                    StringComparer.OrdinalIgnoreCase))
        {
            for (var index = 0; index < featuredMarkets.Length; index++)
            {
                target[index] = featuredMarkets[index];
            }

            _marketSnapshot = markets;
            return;
        }

        target.Clear();
        foreach (var market in featuredMarkets)
        {
            target.Add(market);
        }

        _marketSnapshot = markets;
        EmptyState.Visibility = featuredMarkets.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarketCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MarketCard market })
        {
            MarketSelected?.Invoke(this, market);
        }
    }
}
