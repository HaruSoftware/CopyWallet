using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class WalletsPage : UserControl
{
    public event EventHandler<string>? AddWalletRequested;
    public event EventHandler<string>? OwnWalletRequested;
    public event EventHandler<string>? WalletSelected;
    public event EventHandler<string>? WalletRemoved;
    private IReadOnlyList<WalletPosition> _allPositions = [];
    private IReadOnlyList<WalletTrade> _allTrades = [];
    private string _focusedAddress = "";

    public WalletsPage()
    {
        InitializeComponent();
        TrackedWalletList.ItemsSource = new ObservableCollection<TrackedWallet>();
        PositionList.ItemsSource = new ObservableCollection<WalletPosition>();
        ActivityList.ItemsSource = new ObservableCollection<WalletTrade>();
        ActivityFilter.SelectedValue = "All";
        PositionSort.SelectedValue = "Value";
        SetSelectedTab(showActivity: true);
    }

    public void SetWallets(IEnumerable<TrackedWallet> wallets, string focusedAddress, string ownAddress)
    {
        var target = (ObservableCollection<TrackedWallet>)TrackedWalletList.ItemsSource;
        target.Clear();
        foreach (var wallet in wallets)
        {
            target.Add(wallet);
        }

        WalletCount.Text = $"{target.Count} monitoradas";
        WalletEmptyState.Visibility = target.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FocusedWalletAddress.Text = string.IsNullOrWhiteSpace(focusedAddress)
            ? "Selecione uma carteira monitorada"
            : $"{focusedAddress[..6]}…{focusedAddress[^4..]}";
        _focusedAddress = focusedAddress;
        OwnWalletStatus.Text = string.IsNullOrWhiteSpace(ownAddress)
            ? "Nenhuma carteira própria vinculada."
            : $"Carteira própria vinculada: {ownAddress[..6]}…{ownAddress[^4..]}";
        if (!OwnWalletInput.IsKeyboardFocusWithin)
        {
            OwnWalletInput.Text = ownAddress;
        }
    }

    public void SetDataStatus(string status)
    {
        WalletDataStatus.Text = status;
    }

    public void SetPositions(IReadOnlyList<WalletPosition> positions, string focusedAddress)
    {
        _allPositions = positions;
        _focusedAddress = focusedAddress;
        ApplyPositionFilter();
        PositionEmptyState.Text = string.IsNullOrWhiteSpace(focusedAddress)
            ? "Adicione e selecione uma carteira para consultar posições."
            : "Nenhuma posição aberta encontrada para esta carteira.";
    }

    public void SetActivity(IReadOnlyList<WalletTrade> trades, string focusedAddress)
    {
        _allTrades = trades;
        _focusedAddress = focusedAddress;
        ApplyActivityFilter();
        ActivityEmptyState.Text = string.IsNullOrWhiteSpace(focusedAddress)
            ? "Selecione uma carteira para acompanhar as atividades."
            : "Nenhuma atividade recente encontrada para esta carteira.";
    }

    private void PositionsTab_Click(object sender, RoutedEventArgs e) => SetSelectedTab(showActivity: false);

    private void ActivityTab_Click(object sender, RoutedEventArgs e) => SetSelectedTab(showActivity: true);

    private void SetSelectedTab(bool showActivity)
    {
        ActivityPanel.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        PositionPanel.Visibility = showActivity ? Visibility.Collapsed : Visibility.Visible;
        ActivityFilter.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        PositionSort.Visibility = showActivity ? Visibility.Collapsed : Visibility.Visible;
        SetTabAppearance(PositionsTab, selected: !showActivity);
        SetTabAppearance(ActivityTab, selected: showActivity);
    }

    private static void SetTabAppearance(Button button, bool selected)
    {
        button.SetResourceReference(Control.ForegroundProperty, selected ? "ThemeText" : "ThemeMuted");
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.SetResourceReference(Control.BorderBrushProperty, selected ? "ThemeAccent" : "ThemeBorder");
        button.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
    }

    private void ActivityFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyActivityFilter();
        }
    }

    private void PositionSort_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            ApplyPositionFilter();
        }
    }

    private void ApplyActivityFilter()
    {
        var target = (ObservableCollection<WalletTrade>)ActivityList.ItemsSource;
        var filter = (ActivityFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        target.Clear();
        foreach (var trade in _allTrades
                     .Where(trade => trade.WalletAddress.Equals(_focusedAddress, StringComparison.OrdinalIgnoreCase))
                     .Where(trade => filter == "All" || trade.Side.Equals(filter, StringComparison.OrdinalIgnoreCase)))
        {
            target.Add(trade);
        }

        ActivityEmptyState.Visibility = target.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPositionFilter()
    {
        var target = (ObservableCollection<WalletPosition>)PositionList.ItemsSource;
        var sort = (PositionSort.SelectedItem as ComboBoxItem)?.Tag as string ?? "Value";
        target.Clear();
        var positions = sort == "Pnl"
            ? _allPositions.OrderByDescending(position => position.TotalPnl)
            : _allPositions.OrderByDescending(position => position.CurrentValue);
        foreach (var position in positions)
        {
            target.Add(position);
        }

        PositionEmptyState.Visibility = target.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TrackWallet_Click(object sender, RoutedEventArgs e)
    {
        AddWalletRequested?.Invoke(this, TrackedWalletInput.Text.Trim());
    }

    private void TrackedWalletInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            TrackWallet_Click(sender, new RoutedEventArgs());
        }
    }

    private void OwnWalletInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SetOwnWallet_Click(sender, new RoutedEventArgs());
        }
    }

    private void SetOwnWallet_Click(object sender, RoutedEventArgs e)
    {
        OwnWalletRequested?.Invoke(this, OwnWalletInput.Text.Trim());
    }

    private void TrackedWalletCard_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RemoveWalletButton") is Button removeButton)
        {
            removeButton.Opacity = 1;
            removeButton.IsHitTestVisible = true;
        }
    }

    private void TrackedWalletCard_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Grid grid && grid.FindName("RemoveWalletButton") is Button removeButton)
        {
            removeButton.Opacity = 0;
            removeButton.IsHitTestVisible = false;
        }
    }

    private void TrackedWalletCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border { Tag: string address })
        {
            WalletSelected?.Invoke(this, address);
        }
    }

    private void SelectWallet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address })
        {
            WalletSelected?.Invoke(this, address);
        }
    }

    private void RemoveWallet_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address })
        {
            WalletRemoved?.Invoke(this, address);
        }
    }

    public void ClearTrackedWalletInput() => TrackedWalletInput.Clear();
}
