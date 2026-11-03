using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class MarketsPage : UserControl
{
    public MarketsPage()
    {
        InitializeComponent();
        MarketList.ItemsSource = new ObservableCollection<MarketCard>();
    }

    public void SetMarkets(IReadOnlyList<MarketCard> markets)
    {
        var target = (ObservableCollection<MarketCard>)MarketList.ItemsSource;
        target.Clear();
        foreach (var market in markets)
        {
            target.Add(market);
        }

        EmptyState.Visibility = markets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
