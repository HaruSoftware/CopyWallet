using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WalletCopy.Controls;
using WalletCopy.Models;
using WalletCopy.Pages;
using WalletCopy.Services;

namespace WalletCopy;

public partial class MainWindow : Window
{
    private const string PreviouslySeededDemoWallet = "0x1b414826a2c991d5ae1669a754291626d0df0b56";
    private static readonly Regex WalletAddressPattern = new("^0x[a-fA-F0-9]{40}$", RegexOptions.Compiled);
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(100, 226, 162));
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(242, 140, 140));
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(232, 184, 103));

    private readonly PolymarketApiClient _apiClient = new();
    private readonly UserSettingsService _settingsService = new();
    private readonly ObservableCollection<TrackedWallet> _trackedWallets = [];
    private readonly DashboardPage _dashboardPage = new();
    private readonly WalletsPage _walletsPage = new();
    private readonly MarketsPage _marketsPage = new();
    private readonly PaperTradingPage _paperTradingPage = new();
    private readonly CopyTradesPage _copyTradesPage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly ObservableCollection<CopyTradeRecord> _copiedTrades = [];
    private readonly HashSet<string> _copiedTradeKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> _autoCopyWalletAddresses = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<WalletTrade> _recentTrades = [];
    private string _ownWalletAddress = "";
    private string _focusedWalletAddress = "";
    private string _lastConnectionError = "";
    private string _themePreference = "System";
    private bool _isRefreshing;
    private bool _refreshAgain;

    public MainWindow()
    {
        InitializeComponent();
        _dashboardPage.WalletFilterChanged += DashboardWalletFilterChanged;
        _dashboardPage.CopyTradeRequested += CopyTradeRequested;
        _walletsPage.AddWalletRequested += AddWalletRequested;
        _walletsPage.OwnWalletRequested += OwnWalletRequested;
        _walletsPage.WalletSelected += WalletSelected;
        _walletsPage.WalletRemoved += WalletRemoved;
        _walletsPage.CopyTradeRequested += CopyTradeRequested;
        _walletsPage.CopyAllTradesRequested += CopyAllTradesRequested;
        _walletsPage.AutoCopyChanged += AutoCopyChanged;
        _paperTradingPage.AccountChanged += PaperAccountChanged;
        _copyTradesPage.SetRecords(_copiedTrades);
        _paperTradingPage.SetAccount(new PaperAccount());
        _settingsPage.ThemeChanged += SettingsPageThemeChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshDataAsync();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _refreshTimer.Stop();
        NavigateTo("Dashboard");
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = await _settingsService.LoadAsync();
            _themePreference = settings.ThemePreference is "Light" or "Dark" ? settings.ThemePreference : "System";
            App.ApplyTheme(this, _themePreference);
            _settingsPage.SetTheme(_themePreference);
            var settingsNeedSave = !settings.HasCompletedInitialSetup;
            _ownWalletAddress = IsValidWalletAddress(settings.OwnWalletAddress)
                ? settings.OwnWalletAddress
                : "";
            var savedAddresses = settings.TrackedWalletAddresses ?? [];
            settingsNeedSave |= savedAddresses.RemoveAll(address =>
                !string.IsNullOrWhiteSpace(address)
                && address.Equals(PreviouslySeededDemoWallet, StringComparison.OrdinalIgnoreCase)) > 0;

            foreach (var address in savedAddresses
                         .Where(IsValidWalletAddress)
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _trackedWallets.Add(new TrackedWallet
                {
                    Address = address,
                    IsAutoCopyEnabled = (settings.AutoCopyWalletAddresses ?? [])
                        .Any(item => item.Equals(address, StringComparison.OrdinalIgnoreCase))
                });
            }
            foreach (var address in settings.AutoCopyWalletAddresses ?? [])
            {
                if (IsValidWalletAddress(address))
                {
                    _autoCopyWalletAddresses.Add(address);
                }
            }
            foreach (var record in settings.CopiedTrades ?? [])
            {
                if (!string.IsNullOrWhiteSpace(record.Key) && _copiedTradeKeys.Add(record.Key))
                {
                    _copiedTrades.Add(record);
                }
                _paperTradingPage.SetAccount(settings.PaperAccount ?? new PaperAccount());
            }

            _focusedWalletAddress = IsValidWalletAddress(settings.FocusedWalletAddress)
                                    && (_trackedWallets.Any(wallet =>
                                            wallet.Address.Equals(settings.FocusedWalletAddress, StringComparison.OrdinalIgnoreCase))
                                        || settings.FocusedWalletAddress.Equals(_ownWalletAddress, StringComparison.OrdinalIgnoreCase))
                ? settings.FocusedWalletAddress
                : !string.IsNullOrWhiteSpace(_ownWalletAddress)
                    ? _ownWalletAddress
                    : _trackedWallets.FirstOrDefault()?.Address ?? "";

            settings.HasCompletedInitialSetup = true;
            UpdateWalletPage();
            if (settingsNeedSave)
            {
                await SaveSettingsAsync();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            NotificationPopup.ShowError($"Não foi possível carregar as configurações: {exception.Message}");
        }

        await RefreshDataAsync();
        _refreshTimer.Start();
    }

    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string page })
        {
            NavigateTo(page);
        }
    }

    private void NavigateTo(string page)
    {
        (string Title, UserControl Content) navigation = page switch
        {
            "Wallets" => ("Carteiras", _walletsPage),
            "Markets" => ("Mercados", _marketsPage),
            "PaperTrading" => ("Terminal simulado", _paperTradingPage),
            "CopyTrades" => ("Trades copiados", _copyTradesPage),
            "Settings" => ("Configurações", _settingsPage),
            _ => ("Visão geral", _dashboardPage)
        };

        CurrentPageTitle.Text = navigation.Title;
        PageHost.Content = navigation.Content;
        SetNavigationState(DashboardNavigation, page == "Dashboard");
        SetNavigationState(WalletsNavigation, page == "Wallets");
        SetNavigationState(MarketsNavigation, page == "Markets");
        SetNavigationState(PaperTradingNavigation, page == "PaperTrading");
        SetNavigationState(CopyTradesNavigation, page == "CopyTrades");
        SetNavigationState(SettingsNavigation, page == "Settings");
    }

    private async void SettingsPageThemeChanged(object? sender, string preference)
    {
        _themePreference = preference;
        App.ApplyTheme(this, _themePreference);
        if (await SaveSettingsAsync())
        {
            NotificationPopup.ShowSuccess("Tema salvo localmente.");
        }
    }

    private static void SetNavigationState(Button button, bool isSelected)
    {
        if (isSelected)
        {
            button.SetResourceReference(BackgroundProperty, "ThemeAccent");
            button.SetResourceReference(ForegroundProperty, "ThemeAccentText");
            button.SetResourceReference(BorderBrushProperty, "ThemeAccent");
        }
        else
        {
            button.Background = Brushes.Transparent;
            button.SetResourceReference(ForegroundProperty, "ThemeNav");
            button.BorderBrush = Brushes.Transparent;
        }
    }

    private async void AddWalletRequested(object? sender, string address)
    {
        if (!IsValidWalletAddress(address))
        {
            NotificationPopup.ShowError("Informe um endereço público válido (0x seguido por 40 caracteres hexadecimais).");
            return;
        }

        if (_trackedWallets.Any(wallet => wallet.Address.Equals(address, StringComparison.OrdinalIgnoreCase)))
        {
            NotificationPopup.ShowWarning("Essa carteira já está na sua lista.");
            return;
        }

        if (_ownWalletAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            NotificationPopup.ShowWarning("Essa é a sua carteira própria. Ela já aparece separadamente.");
            return;
        }

        _trackedWallets.Add(new TrackedWallet { Address = address });
        _focusedWalletAddress = address;
        _walletsPage.ClearTrackedWalletInput();
        UpdateWalletPage();
        if (await SaveSettingsAsync())
        {
            NotificationPopup.ShowSuccess($"Carteira {ShortAddress(address)} adicionada.");
        }

        await RefreshDataAsync();
    }

    private async void OwnWalletRequested(object? sender, string address)
    {
        if (!IsValidWalletAddress(address))
        {
            NotificationPopup.ShowError("Informe um endereço público válido para sua carteira.");
            return;
        }

        _ownWalletAddress = address;
        _focusedWalletAddress = address;
        UpdateWalletPage();
        if (await SaveSettingsAsync())
        {
            NotificationPopup.ShowSuccess("Sua carteira foi vinculada para acompanhamento.");
        }

        await RefreshDataAsync();
    }

    private async void WalletSelected(object? sender, string address)
    {
        if (!IsValidWalletAddress(address))
        {
            return;
        }

        _focusedWalletAddress = address;
        UpdateWalletPage();
        await SaveSettingsAsync();
        await RefreshDataAsync();
    }

    private async void DashboardWalletFilterChanged(object? sender, string address)
    {
        if (!IsValidWalletAddress(address)
            || _focusedWalletAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _focusedWalletAddress = address;
        UpdateWalletPage();
        await SaveSettingsAsync();
        await RefreshDataAsync();
    }

    private async void WalletRemoved(object? sender, string address)
    {
        var confirmation = MessageBox.Show(
            "Remover esta carteira da lista de acompanhamento?",
            "Remover carteira",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var wallet = _trackedWallets.FirstOrDefault(item =>
            item.Address.Equals(address, StringComparison.OrdinalIgnoreCase));
        if (wallet is null)
        {
            return;
        }

        _trackedWallets.Remove(wallet);
        if (_focusedWalletAddress.Equals(address, StringComparison.OrdinalIgnoreCase))
        {
            _focusedWalletAddress = !string.IsNullOrWhiteSpace(_ownWalletAddress)
                ? _ownWalletAddress
                : _trackedWallets.FirstOrDefault()?.Address ?? "";
        }

        UpdateWalletPage();
        if (await SaveSettingsAsync())
        {
            NotificationPopup.ShowInformation($"Carteira {ShortAddress(address)} removida da lista.");
        }

        await RefreshDataAsync();
    }

    private async void CopyTradeRequested(object? sender, WalletTrade trade)
    {
        var dialog = new CopyTradeDialog(trade) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            return;
        }

        if (RegisterCopiedTrade(trade, dialog.Draft))
        {
            await SaveSettingsAsync();
            NotificationPopup.ShowSuccess("Trade marcado para cópia. Nenhuma ordem foi enviada.");
        }
        else
        {
            NotificationPopup.ShowInformation("Esse trade já foi marcado para cópia.");
        }
    }

    private async void CopyAllTradesRequested(object? sender, string address)
    {
        var trades = _recentTrades.Where(trade =>
            trade.WalletAddress.Equals(address, StringComparison.OrdinalIgnoreCase)).ToArray();
        var copiedCount = 0;
        foreach (var trade in trades)
        {
            if (RegisterCopiedTrade(trade))
            {
                copiedCount++;
            }
        }
        await SaveSettingsAsync();
        NotificationPopup.ShowInformation(copiedCount == 0
            ? "Nenhum trade novo para copiar nesta carteira."
            : $"{copiedCount} trade(s) marcado(s) para cópia. Nenhuma ordem foi enviada.");
    }

    private async void AutoCopyChanged(object? sender, (string Address, bool Enabled) change)
    {
        if (change.Enabled)
        {
            _autoCopyWalletAddresses.Add(change.Address);
        }

        else
        {
            _autoCopyWalletAddresses.Remove(change.Address);
        }

        var wallet = _trackedWallets.FirstOrDefault(item =>
            item.Address.Equals(change.Address, StringComparison.OrdinalIgnoreCase));
        if (wallet is not null)
        {
            wallet.IsAutoCopyEnabled = change.Enabled;
        }

        await SaveSettingsAsync();
        NotificationPopup.ShowInformation(change.Enabled
            ? "Cópia automática ativada para esta carteira."
            : "Cópia automática desativada para esta carteira.");
    }

    private async void PaperAccountChanged(object? sender, PaperAccount account)
    {
        await SaveSettingsAsync();
        NotificationPopup.ShowInformation("Ordem registrada apenas na conta virtual.");
    }

    private bool RegisterCopiedTrade(WalletTrade trade, CopyTradeDraft? draft = null)
    {
        var key = WalletsPage.GetTradeKey(trade);
        if (!_copiedTradeKeys.Add(key))
        {
            trade.IsCopied = true;
            return false;
        }

        trade.IsCopied = true;
        _copiedTrades.Insert(0, CopyTradeRecord.FromTrade(trade, key, draft));
        _walletsPage.SetCopyState(_copiedTradeKeys);
        _dashboardPage.SetCopyState(_copiedTradeKeys);
        _copyTradesPage.SetRecords(_copiedTrades);
        return true;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDataAsync(showSuccessNotification: true);
    }

    private async Task RefreshDataAsync(bool showSuccessNotification = false)
    {
        if (_isRefreshing)
        {
            _refreshAgain = true;
            return;
        }

        _isRefreshing = true;
        ConnectionStatus.Text = "Sincronizando dados públicos…";
        ConnectionDot.Fill = PendingBrush;
        _walletsPage.SetDataStatus("Sincronizando dados…");
        var errors = new ConcurrentQueue<string>();
        try
        {
            var marketsTask = LoadMarketsAsync(errors);
            var tradeAddresses = _trackedWallets
                .Select(wallet => wallet.Address)
                .Append(_ownWalletAddress)
                .Where(IsValidWalletAddress)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var tradeTasks = tradeAddresses
                .Select(address => LoadTradesAsync(address, errors))
                .ToArray();
            var positionsTask = LoadFocusedPositionsAsync(_focusedWalletAddress, errors);
            var performanceTask = LoadFocusedPerformanceAsync(_focusedWalletAddress, errors);
            var pusdBalanceTask = LoadFocusedPusdBalanceAsync(_focusedWalletAddress, errors);

            await Task.WhenAll(tradeTasks.Cast<Task>()
                .Append(marketsTask)
                .Append(positionsTask)
                .Append(performanceTask)
                .Append(pusdBalanceTask));
            var recentTrades = tradeTasks
                .Where(task => task.Result is not null)
                .SelectMany(task => task.Result!)
                .OrderByDescending(trade => trade.Timestamp)
                .Take(100)
                .ToArray();
            var positions = positionsTask.Result;
            _walletsPage.SetPositions(positions, _focusedWalletAddress);
            foreach (var trade in recentTrades)
            {
                trade.IsCopied = _copiedTradeKeys.Contains(WalletsPage.GetTradeKey(trade));
            }
            _recentTrades = recentTrades;
            var autoCopiedTrades = false;
            foreach (var wallet in _trackedWallets.Where(wallet => _autoCopyWalletAddresses.Contains(wallet.Address)))
            {
                foreach (var trade in recentTrades.Where(trade =>
                             trade.WalletAddress.Equals(wallet.Address, StringComparison.OrdinalIgnoreCase)))
                {
                    autoCopiedTrades |= RegisterCopiedTrade(trade);
                }
            }
            _walletsPage.SetActivity(recentTrades, _focusedWalletAddress);
            _dashboardPage.SetWallets(_trackedWallets, _ownWalletAddress, _focusedWalletAddress);
            _dashboardPage.UpdateData(_trackedWallets.Count, recentTrades, DateTime.Now);
            _dashboardPage.SetCopyState(_copiedTradeKeys);
            _copyTradesPage.SetRecords(_copiedTrades);
            if (autoCopiedTrades)
            {
                await SaveSettingsAsync();
            }
            _dashboardPage.SetWalletPerformance(
                performanceTask.Result,
                pusdBalanceTask.Result,
                _focusedWalletAddress);
            if (marketsTask.Result.Succeeded)
            {
                _marketsPage.SetMarkets(marketsTask.Result.Markets);
                _paperTradingPage.SetMarkets(marketsTask.Result.Markets);
            }

            CurrentWalletLabel.Text = string.IsNullOrWhiteSpace(_focusedWalletAddress)
                ? ""
                : ShortAddress(_focusedWalletAddress);

            var hasSuccessfulRequest = marketsTask.Result.Succeeded
                                       || tradeTasks.Any(task => task.Result is not null)
                                       || (!string.IsNullOrWhiteSpace(_focusedWalletAddress)
                                           && !errors.Any(error => error.StartsWith("Carteira:", StringComparison.Ordinal)));
            if (errors.Count == 0)
            {
                ConnectionStatus.Text = "Polymarket · atualizando a cada 15s";
                ConnectionDot.Fill = ConnectedBrush;
                _walletsPage.SetDataStatus($"Atualizado às {DateTime.Now:HH:mm}");
                _lastConnectionError = "";
                if (showSuccessNotification)
                {
                    NotificationPopup.ShowSuccess("Dados atualizados com sucesso.");
                }
            }
            else
            {
                ConnectionStatus.Text = hasSuccessfulRequest ? "Conectado · dados parciais" : "Falha na conexão";
                ConnectionDot.Fill = hasSuccessfulRequest ? PendingBrush : DisconnectedBrush;
                _walletsPage.SetDataStatus(hasSuccessfulRequest ? "Dados parciais · verifique os avisos" : "Não foi possível carregar os dados");
                var errorSummary = string.Join("  •  ", errors.Distinct());
                if (!errorSummary.Equals(_lastConnectionError, StringComparison.Ordinal))
                {
                    NotificationPopup.ShowError(errorSummary);
                    _lastConnectionError = errorSummary;
                }
            }
        }
        catch (Exception exception)
        {
            ConnectionStatus.Text = "Erro ao atualizar dados";
            ConnectionDot.Fill = DisconnectedBrush;
            _walletsPage.SetDataStatus("Erro ao carregar dados");
            NotificationPopup.ShowError($"Falha inesperada ao atualizar: {exception.Message}");
        }
        finally
        {
            _isRefreshing = false;
            if (_refreshAgain)
            {
                _refreshAgain = false;
                await RefreshDataAsync();
            }
        }
    }

    private async Task<MarketLoadResult> LoadMarketsAsync(ConcurrentQueue<string> errors)
    {
        try
        {
            var markets = await _apiClient.GetTrendingMarketsAsync();
            return new MarketLoadResult(true, markets);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            errors.Enqueue($"Mercados: {exception.Message}");
            return new MarketLoadResult(false, []);
        }
    }

    private async Task<IReadOnlyList<WalletTrade>?> LoadTradesAsync(string address, ConcurrentQueue<string> errors)
    {
        try
        {
            return await _apiClient.GetRecentTradesAsync(address);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            errors.Enqueue($"Trades {ShortAddress(address)}: {exception.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<WalletPosition>> LoadFocusedPositionsAsync(
        string walletAddress,
        ConcurrentQueue<string> errors)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return [];
        }

        try
        {
            return await _apiClient.GetOpenPositionsAsync(walletAddress);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            errors.Enqueue($"Carteira: {exception.Message}");
            return [];
        }
    }

    private async Task<WalletPerformance?> LoadFocusedPerformanceAsync(
        string walletAddress,
        ConcurrentQueue<string> errors)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return null;
        }

        try
        {
            return await _apiClient.GetWalletPerformanceAsync(walletAddress);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            errors.Enqueue($"Desempenho {ShortAddress(walletAddress)}: {exception.Message}");
            return null;
        }
    }

    private async Task<decimal?> LoadFocusedPusdBalanceAsync(
        string walletAddress,
        ConcurrentQueue<string> errors)
    {
        if (string.IsNullOrWhiteSpace(walletAddress))
        {
            return null;
        }

        try
        {
            return await _apiClient.GetWalletPusdBalanceAsync(walletAddress);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            errors.Enqueue($"Saldo pUSD {ShortAddress(walletAddress)}: {exception.Message}");
            return null;
        }
    }

    private async Task<bool> SaveSettingsAsync()
    {
        try
        {
            await _settingsService.SaveAsync(new UserSettings
            {
                ThemePreference = _themePreference,
                OwnWalletAddress = _ownWalletAddress,
                FocusedWalletAddress = _focusedWalletAddress,
                HasCompletedInitialSetup = true,
                TrackedWalletAddresses = _trackedWallets.Select(wallet => wallet.Address).ToList(),
                AutoCopyWalletAddresses = _autoCopyWalletAddresses.ToList(),
                CopiedTrades = _copiedTrades.ToList(),
                PaperAccount = _paperTradingPage.GetAccount()
            });
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            NotificationPopup.ShowError($"Não foi possível salvar suas configurações: {exception.Message}");
            return false;
        }
    }

    private void UpdateWalletPage()
    {
        _walletsPage.SetWallets(_trackedWallets, _focusedWalletAddress, _ownWalletAddress);
        _dashboardPage.SetWallets(_trackedWallets, _ownWalletAddress, _focusedWalletAddress);
    }

    private static bool IsValidWalletAddress(string? address)
    {
        return !string.IsNullOrWhiteSpace(address) && WalletAddressPattern.IsMatch(address);
    }

    private static string ShortAddress(string address) => $"{address[..6]}…{address[^4..]}";

    private sealed record MarketLoadResult(bool Succeeded, IReadOnlyList<MarketCard> Markets);
}
