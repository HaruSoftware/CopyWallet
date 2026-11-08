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
    private readonly WalletAnalysisPage _walletAnalysisPage = new();
    private readonly MarketsPage _marketsPage = new();
    private readonly PaperTradingPage _paperTradingPage;
    private readonly ArbitrageMonitorPage _arbitrageMonitorPage;
    private readonly CopyTradesPage _copyTradesPage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly PolymarketClobClient _clobClient = new();
    private readonly TradingCredentialService _credentialService = new();
    private TradingCredentials? _tradingCredentials;
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
        _paperTradingPage = new PaperTradingPage(_apiClient);
        _arbitrageMonitorPage = new ArbitrageMonitorPage(_apiClient);
        _dashboardPage.WalletFilterChanged += DashboardWalletFilterChanged;
        _dashboardPage.CopyTradeRequested += CopyTradeRequested;
        _walletsPage.AddWalletRequested += AddWalletRequested;
        _walletsPage.OwnWalletRequested += OwnWalletRequested;
        _walletsPage.WalletSelected += WalletSelected;
        _walletAnalysisPage.AnalysisRequested += WalletAnalysisRequested;
        _walletsPage.WalletRemoved += WalletRemoved;
        _walletsPage.CopyTradeRequested += CopyTradeRequested;
        _walletsPage.CopyAllTradesRequested += CopyAllTradesRequested;
        _walletsPage.AutoCopyChanged += AutoCopyChanged;
        _marketsPage.MarketSelected += MarketsPage_MarketSelected;
        _paperTradingPage.AccountChanged += PaperAccountChanged;
        _copyTradesPage.SetRecords(_copiedTrades);
        _paperTradingPage.SetAccount(new PaperAccount());
        _settingsPage.ThemeChanged += SettingsPageThemeChanged;
        _settingsPage.CredentialsChanged += SettingsPageCredentialsChanged;
        _refreshTimer.Tick += async (_, _) => await RefreshDataAsync();
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _refreshTimer.Stop();
            _arbitrageMonitorPage.StopMonitoring();
        };
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
            }
            _paperTradingPage.SetAccount(settings.PaperAccount ?? new PaperAccount());

            _focusedWalletAddress = IsValidWalletAddress(settings.FocusedWalletAddress)
                                    && (_trackedWallets.Any(wallet =>
                                            wallet.Address.Equals(settings.FocusedWalletAddress, StringComparison.OrdinalIgnoreCase))
                                        || settings.FocusedWalletAddress.Equals(_ownWalletAddress, StringComparison.OrdinalIgnoreCase))
                ? settings.FocusedWalletAddress
                : !string.IsNullOrWhiteSpace(_ownWalletAddress)
                    ? _ownWalletAddress
                    : _trackedWallets.FirstOrDefault()?.Address ?? "";

            _tradingCredentials = await _credentialService.LoadCredentialsAsync();
            _settingsPage.SetTradingCredentials(_tradingCredentials);
            UpdateClobStatusUi();
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
            "WalletAnalysis" => ("Análise de carteira", _walletAnalysisPage),
            "Markets" => ("Mercados", _marketsPage),
            "PaperTrading" => ("Terminal simulado", _paperTradingPage),
            "ArbitrageMonitor" => ("Monitor de book", _arbitrageMonitorPage),
            "CopyTrades" => ("Trades copiados", _copyTradesPage),
            "Settings" => ("Configurações", _settingsPage),
            _ => ("Visão geral", _dashboardPage)
        };

        CurrentPageTitle.Text = navigation.Title;
        PageHost.Content = navigation.Content;
        SetNavigationState(DashboardNavigation, page == "Dashboard");
        SetNavigationState(WalletsNavigation, page == "Wallets");
        SetNavigationState(WalletAnalysisNavigation, page == "WalletAnalysis");
        SetNavigationState(MarketsNavigation, page == "Markets");
        SetNavigationState(PaperTradingNavigation, page == "PaperTrading");
        SetNavigationState(ArbitrageNavigation, page == "ArbitrageMonitor");
        SetNavigationState(CopyTradesNavigation, page == "CopyTrades");
        SetNavigationState(SettingsNavigation, page == "Settings");
    }

    private void MarketsPage_MarketSelected(object? sender, MarketCard market)
    {
        NavigateTo("PaperTrading");
        _paperTradingPage.SelectMarket(market.ConditionId);
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

    private void UpdateClobStatusUi()
    {
        var isConnected = _tradingCredentials is not null && _tradingCredentials.IsConfigured;
        _copyTradesPage.UpdateConnectionStatus(isConnected);
        if (isConnected)
        {
            SidebarModeDot.Fill = ConnectedBrush;
            SidebarModeTitle.Text = "EXECUÇÃO CLOB ATIVA";
            SidebarModeSubtitle.Text = "Pronto para enviar e gerenciar ordens reais na Polymarket.";
        }
        else
        {
            SidebarModeDot.Fill = PendingBrush;
            SidebarModeTitle.Text = "MODO CONSULTA / PAPER";
            SidebarModeSubtitle.Text = "Conecte sua conta em Configurações para habilitar ordens reais.";
        }
    }

    private void SettingsPageCredentialsChanged(object? sender, TradingCredentials? credentials)
    {
        _tradingCredentials = credentials;
        UpdateClobStatusUi();
        if (credentials is not null && credentials.IsConfigured)
        {
            NotificationPopup.ShowSuccess("Conta Polymarket conectada com sucesso! Execução real de ordens habilitada.");
        }
        else
        {
            NotificationPopup.ShowInformation("Credenciais de trading desconectadas.");
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

    private async void WalletAnalysisRequested(object? sender, string address)
    {
        if (!IsValidWalletAddress(address))
        {
            const string message = "Informe um endereço público válido (0x seguido por 40 caracteres hexadecimais).";
            _walletAnalysisPage.SetError(message);
            NotificationPopup.ShowError(message);
            return;
        }

        _walletAnalysisPage.SetLoading(address);
        try
        {
            var trades = await _apiClient.GetRecentTradesAsync(address);
            _walletAnalysisPage.SetAnalysis(address, trades);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or System.Text.Json.JsonException
            or InvalidDataException
            or IOException)
        {
            var message = $"Não foi possível analisar a carteira: {exception.Message}";
            _walletAnalysisPage.SetError(message);
            NotificationPopup.ShowError(message);
        }
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
        var hasCreds = _tradingCredentials is not null && _tradingCredentials.IsConfigured;
        var dialog = new CopyTradeDialog(trade, hasCreds) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Draft is null)
        {
            return;
        }

        var draft = dialog.Draft;
        var key = WalletsPage.GetTradeKey(trade);

        if (draft.ExecutionTarget == "Real")
        {
            if (!hasCreds)
            {
                NotificationPopup.ShowError("Credenciais de trading não configuradas. Acesse a aba Configurações.");
                return;
            }

            var record = CopyTradeRecord.FromTrade(trade, key, draft);
            _copiedTradeKeys.Add(key);
            trade.IsCopied = true;
            _copiedTrades.Insert(0, record);
            _walletsPage.SetCopyState(_copiedTradeKeys);
            _dashboardPage.SetCopyState(_copiedTradeKeys);
            _copyTradesPage.SetRecords(_copiedTrades);
            await SaveSettingsAsync();

            NotificationPopup.ShowInformation("Enviando ordem real para a Polymarket CLOB…");

            if (draft.IsSplit)
            {
                NotificationPopup.ShowInformation("Executando TEST SPLIT real (compra simultânea de ambos os lados)…");
                try
                {
                    var outcomes = await _apiClient.GetMarketOutcomesByConditionAsync(draft.ConditionId);
                    if (outcomes.Count < 2 || string.IsNullOrWhiteSpace(outcomes[0].TokenId) || string.IsNullOrWhiteSpace(outcomes[1].TokenId))
                    {
                        record.ExecutionStatus = "Falhou";
                        record.ErrorMessage = "Não foi possível obter os tokens dos dois lados do mercado para o split.";
                        await SaveSettingsAsync();
                        _copyTradesPage.SetRecords(_copiedTrades);
                        NotificationPopup.ShowError("Mercado não possui os 2 tokens necessários para Test Split.");
                        return;
                    }

                    var order1Req = new ClobOrderRequest
                    {
                        TokenId = outcomes[0].TokenId,
                        ConditionId = draft.ConditionId,
                        MarketTitle = trade.MarketTitle,
                        Outcome = outcomes[0].Name,
                        Side = "BUY",
                        OrderType = draft.OrderType,
                        Size = draft.Size,
                        Price = outcomes[0].Price > 0m ? outcomes[0].Price : 0.50m,
                        MaxSlippagePercent = draft.MaxSlippagePercent,
                        IsNegRisk = false,
                        Expiration = 0
                    };
                    var order2Req = new ClobOrderRequest
                    {
                        TokenId = outcomes[1].TokenId,
                        ConditionId = draft.ConditionId,
                        MarketTitle = trade.MarketTitle,
                        Outcome = outcomes[1].Name,
                        Side = "BUY",
                        OrderType = draft.OrderType,
                        Size = draft.Size,
                        Price = outcomes[1].Price > 0m ? outcomes[1].Price : 0.50m,
                        MaxSlippagePercent = draft.MaxSlippagePercent,
                        IsNegRisk = false,
                        Expiration = 0
                    };

                    var res1 = await _clobClient.PostOrderAsync(_tradingCredentials!, order1Req);
                    var res2 = await _clobClient.PostOrderAsync(_tradingCredentials!, order2Req);

                    if (res1.Success || res2.Success)
                    {
                        record.ClobOrderId = $"Split: {ShortAddress(res1.OrderId ?? "")} / {ShortAddress(res2.OrderId ?? "")}";
                        record.ExecutionStatus = (res1.Success && res2.Success) ? "Preenchido (Split Real)" : "Split Parcial";
                        record.AmountLabel = $"${(res1.TotalCost + res2.TotalCost):N2}";
                        record.ErrorMessage = res1.Success ? (res2.Success ? null : res2.ErrorMessage) : res1.ErrorMessage;
                        await SaveSettingsAsync();
                        _copyTradesPage.SetRecords(_copiedTrades);
                        NotificationPopup.ShowSuccess($"Test Split real enviado com sucesso! Custo total: ${res1.TotalCost + res2.TotalCost:N2}");
                    }
                    else
                    {
                        record.ExecutionStatus = "Falhou";
                        record.ErrorMessage = $"Lado 1: {res1.ErrorMessage}; Lado 2: {res2.ErrorMessage}";
                        await SaveSettingsAsync();
                        _copyTradesPage.SetRecords(_copiedTrades);
                        NotificationPopup.ShowError($"Test Split rejeitado: {record.ErrorMessage}");
                    }
                }
                catch (Exception ex)
                {
                    record.ExecutionStatus = "Erro";
                    record.ErrorMessage = ex.Message;
                    await SaveSettingsAsync();
                    _copyTradesPage.SetRecords(_copiedTrades);
                    NotificationPopup.ShowError($"Erro no Test Split: {ex.Message}");
                }
                return;
            }

            try
            {
                var clobRequest = new ClobOrderRequest
                {
                    TokenId = draft.TokenId,
                    ConditionId = draft.ConditionId,
                    MarketTitle = trade.MarketTitle,
                    Outcome = draft.Position,
                    Side = draft.Side,
                    OrderType = draft.OrderType,
                    Size = draft.Size,
                    Price = draft.Price,
                    MaxSlippagePercent = draft.MaxSlippagePercent,
                    IsNegRisk = false,
                    Expiration = 0
                };

                var clobResult = await _clobClient.PostOrderAsync(_tradingCredentials!, clobRequest);
                if (clobResult.Success)
                {
                    record.ClobOrderId = clobResult.OrderId;
                    record.ExecutionStatus = clobResult.Status;
                    record.AmountLabel = $"${clobResult.TotalCost:N2}";
                    record.ErrorMessage = null;
                    await SaveSettingsAsync();
                    _copyTradesPage.SetRecords(_copiedTrades);
                    NotificationPopup.ShowSuccess($"Ordem real enviada! Status: {clobResult.Status} · ID: {ShortAddress(clobResult.OrderId)}");
                }
                else
                {
                    record.ExecutionStatus = "Falhou";
                    record.ErrorMessage = clobResult.ErrorMessage;
                    await SaveSettingsAsync();
                    _copyTradesPage.SetRecords(_copiedTrades);
                    NotificationPopup.ShowError($"Ordem rejeitada: {clobResult.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                record.ExecutionStatus = "Erro";
                record.ErrorMessage = ex.Message;
                await SaveSettingsAsync();
                _copyTradesPage.SetRecords(_copiedTrades);
                NotificationPopup.ShowError($"Erro ao enviar ordem: {ex.Message}");
            }
            return;
        }

        if (RegisterCopiedTrade(trade, dialog.Draft))
        {
            await SaveSettingsAsync();
            if (draft.IsSplit)
            {
                try
                {
                    var paperMarket = new PaperMarket
                    {
                        Id = draft.ConditionId,
                        ConditionId = draft.ConditionId,
                        Title = trade.MarketTitle,
                        Outcomes = [
                            new MarketOutcome { Name = "Up", TokenId = draft.TokenId, Price = 0.5m },
                            new MarketOutcome { Name = "Down", TokenId = draft.TokenId + "_opp", Price = 0.5m }
                        ]
                    };
                    _paperTradingPage.SimulateSplitDirect(paperMarket, draft.Size * draft.Price);
                }
                catch { }
            }
            NotificationPopup.ShowSuccess(draft.IsSplit ? "Test Split simulado executado com sucesso." : "Trade marcado para cópia. Nenhuma ordem foi enviada.");
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
        NotificationPopup.ShowInformation("Conta virtual atualizada. Nenhuma ordem real foi enviada.");
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
            var paperResolutionsTask = LoadPaperResolutionsAsync(errors);

            await Task.WhenAll(tradeTasks.Cast<Task>()
                .Append(marketsTask)
                .Append(positionsTask)
                .Append(performanceTask)
                .Append(pusdBalanceTask)
                .Append(paperResolutionsTask));
            var resolutionResult = _paperTradingPage.ApplyResolutions(paperResolutionsTask.Result);
            if (resolutionResult.SettledPositions > 0 || resolutionResult.CancelledOrders > 0)
            {
                await SaveSettingsAsync();
                var resolutionMessages = new List<string>();
                if (resolutionResult.SettledPositions > 0)
                {
                    resolutionMessages.Add(
                        $"{resolutionResult.SettledPositions} posição(ões) simulada(s) liquidada(s)");
                }
                if (resolutionResult.CancelledOrders > 0)
                {
                    resolutionMessages.Add(
                        $"{resolutionResult.CancelledOrders} ordem(ns) GTC cancelada(s) porque o mercado foi encerrado");
                }

                NotificationPopup.ShowInformation(string.Join(". ", resolutionMessages) + ".");
            }

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
                _arbitrageMonitorPage.SetMarkets(marketsTask.Result.Markets);
            }
            try
            {
                if (await _paperTradingPage.RefreshOpenOrdersAsync())
                {
                    await SaveSettingsAsync();
                    NotificationPopup.ShowInformation("Uma ordem GTC simulada recebeu novo preenchimento.");
                }
            }
            catch (Exception exception) when (
                exception is HttpRequestException
                or TaskCanceledException
                or System.Text.Json.JsonException
                or InvalidDataException
                or IOException)
            {
                errors.Enqueue($"Ordens simuladas: {exception.Message}");
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

    private async Task<IReadOnlyList<MarketCard>> LoadPaperResolutionsAsync(ConcurrentQueue<string> errors)
    {
        var account = _paperTradingPage.GetAccount();
        var conditionIds = account.Positions
            .Where(position => position.Status == "Open")
            .Select(position => position.ConditionId)
            .Concat(account.Orders
                .Where(order => (order.Status is "Open" or "Partial fill") && order.OrderType == "GTC")
                .Select(order => order.ConditionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (conditionIds.Length == 0)
        {
            return [];
        }

        try
        {
            return await _apiClient.GetResolvedMarketsAsync(conditionIds);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
            or TaskCanceledException
            or System.Text.Json.JsonException
            or InvalidDataException
            or IOException)
        {
            errors.Enqueue($"Resolução das posições simuladas: {exception.Message}");
            return [];
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
        _walletAnalysisPage.SetWallets(
            _trackedWallets.Select(wallet => wallet.Address).Append(_ownWalletAddress),
            _focusedWalletAddress);
    }

    private static bool IsValidWalletAddress(string? address)
    {
        return !string.IsNullOrWhiteSpace(address) && WalletAddressPattern.IsMatch(address);
    }

    private static string ShortAddress(string address) => $"{address[..6]}…{address[^4..]}";

    private sealed record MarketLoadResult(bool Succeeded, IReadOnlyList<MarketCard> Markets);
}
