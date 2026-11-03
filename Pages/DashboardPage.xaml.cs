using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class DashboardPage : UserControl
{
    private readonly ObservableCollection<TrackedWallet> _walletOptions = [];
    private readonly ObservableCollection<WalletTrade> _allTrades = [];
    private bool _isUpdatingWallets;

    public event EventHandler<string>? WalletFilterChanged;
    public event EventHandler<WalletTrade>? CopyTradeRequested;

    public DashboardPage()
    {
        InitializeComponent();
        TradeList.ItemsSource = new ObservableCollection<WalletTrade>();
        WalletFilter.ItemsSource = _walletOptions;
    }

    public void UpdateData(int walletCount, IReadOnlyList<WalletTrade> trades, DateTime updatedAt)
    {
        WalletCount.Text = walletCount.ToString();
        LastUpdated.Text = updatedAt.ToString("HH:mm:ss");
        _allTrades.Clear();
        foreach (var trade in trades)
        {
            _allTrades.Add(trade);
        }

        ApplyTradeFilter();
    }

    public void SetWalletPerformance(
        WalletPerformance? performance,
        decimal? pusdBalance,
        string selectedAddress)
    {
        PlayerAddress.Text = string.IsNullOrWhiteSpace(selectedAddress)
            ? "Selecione uma carteira para consultar os resultados."
            : $"{selectedAddress[..6]}…{selectedAddress[^4..]}";
        WalletPerformanceCard.Visibility = string.IsNullOrWhiteSpace(selectedAddress)
            ? Visibility.Collapsed
            : Visibility.Visible;

        DailyPnl.Text = performance?.DailyPnlLabel ?? "Indisponível";
        AllTimePnl.Text = performance?.AllTimePnlLabel ?? "Indisponível";
        PortfolioValue.Text = performance?.PortfolioValueLabel ?? "Indisponível";
        PusdBalance.Text = pusdBalance is decimal balance
            ? $"${balance.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("en-US"))}"
            : "Indisponível";
        DailyPnl.Foreground = GetPnlBrush(performance?.DailyPnl);
        AllTimePnl.Foreground = GetPnlBrush(performance?.AllTimePnl);
    }

    public void SetWallets(IEnumerable<TrackedWallet> wallets, string ownAddress, string selectedAddress)
    {
        _isUpdatingWallets = true;
        _walletOptions.Clear();
        foreach (var wallet in wallets)
        {
            _walletOptions.Add(wallet);
        }

        if (!string.IsNullOrWhiteSpace(ownAddress)
            && !_walletOptions.Any(wallet => wallet.Address.Equals(ownAddress, StringComparison.OrdinalIgnoreCase)))
        {
            _walletOptions.Add(new TrackedWallet { Address = ownAddress });
        }

        WalletFilter.IsEnabled = _walletOptions.Count > 0;
        WalletFilter.SelectedValue = _walletOptions.Any(wallet =>
            wallet.Address.Equals(selectedAddress, StringComparison.OrdinalIgnoreCase))
            ? selectedAddress
            : null;
        _isUpdatingWallets = false;
        ApplyTradeFilter();
    }

    private void WalletFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingWallets || WalletFilter.SelectedValue is not string address)
        {
            return;
        }

        ApplyTradeFilter();
        WalletFilterChanged?.Invoke(this, address);
    }

    private void ApplyTradeFilter()
    {
        var selectedAddress = WalletFilter.SelectedValue as string;
        var filteredTrades = string.IsNullOrWhiteSpace(selectedAddress)
            ? Array.Empty<WalletTrade>()
            : _allTrades.Where(trade => trade.WalletAddress.Equals(
                    selectedAddress, StringComparison.OrdinalIgnoreCase))
                .ToArray();

        var target = (ObservableCollection<WalletTrade>)TradeList.ItemsSource;
        target.Clear();
        foreach (var trade in filteredTrades)
        {
            target.Add(trade);
        }

        TradeCount.Text = filteredTrades.Length.ToString();
        EmptyState.Text = _walletOptions.Count == 0
            ? "Adicione uma carteira na página Carteiras para acompanhar seus trades."
            : selectedAddress is null
                ? "Selecione uma carteira para filtrar os trades."
                : "Nenhum trade recente encontrado para esta carteira.";
        EmptyState.Visibility = filteredTrades.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static System.Windows.Media.Brush GetPnlBrush(decimal? pnl)
    {
        if (pnl is null)
        {
            return new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(139, 148, 163));
        }

        return pnl >= 0m
            ? new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(66, 215, 125))
            : new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 113, 106));
    }

    public void SetCopyState(ISet<string> copiedTradeKeys)
    {
        foreach (var trade in _allTrades)
        {
            trade.IsCopied = copiedTradeKeys.Contains(WalletsPage.GetTradeKey(trade));
        }
        TradeList.Items.Refresh();
    }

    private void CopyTrade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: WalletTrade trade })
        {
            CopyTradeRequested?.Invoke(this, trade);
        }
    }
}
