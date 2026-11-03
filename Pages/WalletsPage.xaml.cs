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

    public WalletsPage()
    {
        InitializeComponent();
        TrackedWalletList.ItemsSource = new ObservableCollection<TrackedWallet>();
        PositionList.ItemsSource = new ObservableCollection<WalletPosition>();
        ActivityList.ItemsSource = new ObservableCollection<WalletTrade>();
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
        if (!OwnWalletInput.IsKeyboardFocusWithin)
        {
            OwnWalletInput.Text = ownAddress;
        }
    }

    public void SetPositions(IReadOnlyList<WalletPosition> positions, string focusedAddress)
    {
        var target = (ObservableCollection<WalletPosition>)PositionList.ItemsSource;
        target.Clear();
        foreach (var position in positions)
        {
            target.Add(position);
        }

        PositionEmptyState.Visibility = positions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PositionEmptyState.Text = string.IsNullOrWhiteSpace(focusedAddress)
            ? "Adicione e selecione uma carteira para consultar posições."
            : "Nenhuma posição aberta encontrada para esta carteira.";
    }

    public void SetActivity(IReadOnlyList<WalletTrade> trades, string focusedAddress)
    {
        var target = (ObservableCollection<WalletTrade>)ActivityList.ItemsSource;
        target.Clear();
        foreach (var trade in trades.Where(trade =>
                     trade.WalletAddress.Equals(focusedAddress, StringComparison.OrdinalIgnoreCase)))
        {
            target.Add(trade);
        }

        ActivityEmptyState.Text = string.IsNullOrWhiteSpace(focusedAddress)
            ? "Selecione uma carteira para acompanhar as atividades."
            : "Nenhuma atividade recente encontrada para esta carteira.";
        ActivityEmptyState.Visibility = target.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionsTab_Click(object sender, RoutedEventArgs e) => SetSelectedTab(showActivity: false);

    private void ActivityTab_Click(object sender, RoutedEventArgs e) => SetSelectedTab(showActivity: true);

    private void SetSelectedTab(bool showActivity)
    {
        ActivityPanel.Visibility = showActivity ? Visibility.Visible : Visibility.Collapsed;
        PositionPanel.Visibility = showActivity ? Visibility.Collapsed : Visibility.Visible;
        SetTabAppearance(PositionsTab, selected: !showActivity);
        SetTabAppearance(ActivityTab, selected: showActivity);
    }

    private static void SetTabAppearance(Button button, bool selected)
    {
        button.Foreground = selected
            ? System.Windows.Media.Brushes.White
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(139, 148, 163));
        button.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
        button.BorderBrush = selected
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 226, 162))
            : System.Windows.Media.Brushes.Transparent;
        button.BorderThickness = new Thickness(0, 0, 0, selected ? 2 : 0);
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
