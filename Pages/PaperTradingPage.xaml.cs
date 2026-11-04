using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WalletCopy.Models;
using WalletCopy.Services;

namespace WalletCopy.Pages;

public partial class PaperTradingPage : UserControl
{
    private readonly ObservableCollection<MarketCard> _markets = [];
    private readonly ObservableCollection<MarketCard> _visibleMarkets = [];
    private readonly ObservableCollection<MarketOutcome> _outcomes = [];
    private readonly ObservableCollection<PaperOrder> _orders = [];
    private readonly ObservableCollection<PaperPosition> _positions = [];
    private readonly PaperTradingService _service = new();
    private readonly PolymarketApiClient _apiClient;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _liveBookUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _marketCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, MarketOrderBook> _liveBooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketOrderBook> _pendingLiveBooks = new(StringComparer.Ordinal);
    private PaperAccount _account = new();
    private MarketOrderBook? _currentOrderBook;
    private CancellationTokenSource? _bookLoadCancellation;
    private CancellationTokenSource? _marketStreamCancellation;
    private string? _loadedOrderBookTokenId;
    private string? _activeMarketId;
    private bool _isRefreshingOutcomes;
    private bool _isSettingDefaultPrice;
    private bool _hasUserEditedPrice;
    private IReadOnlyList<MarketCard>? _marketSnapshot;

    public event EventHandler<PaperAccount>? AccountChanged;

    public PaperTradingPage(PolymarketApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponent();
        MarketSelector.ItemsSource = _visibleMarkets;
        OutcomeSelector.ItemsSource = _outcomes;
        OrderList.ItemsSource = _orders;
        PositionList.ItemsSource = _positions;
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildMarketList((MarketSelector.SelectedItem as MarketCard)?.ConditionId);
        };
        _liveBookUpdateTimer.Tick += (_, _) => ApplyPendingBookUpdates();
        _marketCountdownTimer.Tick += (_, _) => UpdateMarketCountdown();
        IsVisibleChanged += PaperTradingPage_IsVisibleChanged;
    }

    public void SetMarkets(IEnumerable<MarketCard> markets)
    {
        if (ReferenceEquals(_marketSnapshot, markets))
        {
            return;
        }

        var nextMarkets = markets.ToArray();
        _service.UpdatePositionPrices(_account, nextMarkets);
        var sameMarketIds = _markets.Count == nextMarkets.Length
                            && _markets.Select(market => market.ConditionId)
                                .SequenceEqual(
                                    nextMarkets.Select(market => market.ConditionId),
                                    StringComparer.OrdinalIgnoreCase);
        _marketSnapshot = markets as IReadOnlyList<MarketCard> ?? nextMarkets;
        if (sameMarketIds)
        {
            var currentMarketId = (MarketSelector.SelectedItem as MarketCard)?.ConditionId;
            for (var index = 0; index < nextMarkets.Length; index++)
            {
                _markets[index] = nextMarkets[index];
            }

            RebuildMarketList(currentMarketId);
            if (IsVisible)
            {
                RefreshPositions();
            }
            RefreshSummary();
            return;
        }

        var selectedMarketId = (MarketSelector.SelectedItem as MarketCard)?.ConditionId;
        _markets.Clear();
        foreach (var market in nextMarkets)
        {
            _markets.Add(market);
        }

        RebuildMarketList(selectedMarketId);
        RefreshCollections();
    }

    public void SelectMarket(string conditionId)
    {
        _searchDebounce.Stop();
        MarketSearchInput.Text = "";
        MarketFilterSelector.SelectedIndex = 0;
        RebuildMarketList();

        var market = _visibleMarkets.FirstOrDefault(item =>
            item.ConditionId.Equals(conditionId, StringComparison.OrdinalIgnoreCase));
        if (market is null)
        {
            return;
        }

        MarketSelector.SelectedItem = market;
        MarketSelector.ScrollIntoView(market);
    }

    public void SetAccount(PaperAccount account)
    {
        _account = account;
        _account.Orders ??= [];
        _account.Positions ??= [];
        RefreshCollections();
    }

    public PaperAccount GetAccount() => _account;

    public (int SettledPositions, int CancelledOrders) ApplyResolutions(
        IEnumerable<MarketCard> resolvedMarkets)
    {
        var result = _service.ResolvePositions(_account, resolvedMarkets);
        if (result.SettledPositions > 0 || result.CancelledOrders > 0)
        {
            RefreshCollections();
        }

        return result;
    }

    public async Task<bool> RefreshOpenOrdersAsync()
    {
        var openOrders = _account.Orders
            .Where(order => (order.Status is "Open" or "Partial fill")
                            && order.OrderType == "GTC"
                            && order.RemainingShares > 0m
                            && !string.IsNullOrWhiteSpace(order.TokenId))
            .ToArray();
        var orderBooks = new Dictionary<string, MarketOrderBook>(StringComparer.Ordinal);
        foreach (var tokenId in openOrders.Select(order => order.TokenId).Distinct(StringComparer.Ordinal))
        {
            orderBooks[tokenId] = await _apiClient.GetOrderBookAsync(tokenId);
        }

        var changed = false;
        foreach (var order in openOrders)
        {
            changed |= _service.MatchOpenOrder(_account, order, orderBooks[order.TokenId]);
        }

        if (changed)
        {
            RefreshCollections();
        }

        return changed;
    }

    private void RebuildMarketList(string? selectedMarketId = null)
    {
        var query = MarketSearchInput.Text.Trim();
        var filter = (MarketFilterSelector.SelectedItem as ComboBoxItem)?.Content as string ?? "Todos";
        var filteredMarkets = _markets.Where(market =>
            (filter == "Todos"
             || (filter == "Rápidos" && !string.IsNullOrEmpty(market.TimeframeLabel))
             || (filter == "Em alta" && string.IsNullOrEmpty(market.TimeframeLabel)))
            && (query.Length == 0
                || market.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || market.EventTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || market.Slug.Contains(query, StringComparison.OrdinalIgnoreCase)));

        _visibleMarkets.Clear();
        foreach (var market in filteredMarkets)
        {
            _visibleMarkets.Add(market);
        }

        if (!string.IsNullOrWhiteSpace(selectedMarketId))
        {
            MarketSelector.SelectedItem = _visibleMarkets.FirstOrDefault(market =>
                market.ConditionId.Equals(selectedMarketId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async void MarketSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsVisible || MarketSelector.SelectedItem is not MarketCard market)
        {
            return;
        }

        if (string.Equals(_activeMarketId, market.ConditionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await OpenMarketAsync(market);
    }

    private async Task OpenMarketAsync(MarketCard market)
    {
        StopMarketStream();
        _activeMarketId = market.ConditionId;
        _liveBooks.Clear();
        _pendingLiveBooks.Clear();
        _currentOrderBook = null;
        _loadedOrderBookTokenId = null;
        _hasUserEditedPrice = false;
        SelectedMarketText.Text = market.Title;
        MarketStreamStatus.Text = "Carregando book público…";
        MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(232, 184, 103));
        UpdateMarketCountdown();

        _isRefreshingOutcomes = true;
        try
        {
            _outcomes.Clear();
            foreach (var outcome in market.Outcomes)
            {
                _outcomes.Add(outcome);
            }

            OutcomeSelector.SelectedIndex = _outcomes.Count > 0 ? 0 : -1;
        }
        finally
        {
            _isRefreshingOutcomes = false;
        }

        _currentOrderBook = null;
        _loadedOrderBookTokenId = null;
        UpdateOrderBookLabels(null);
        if (_outcomes.Count == 0)
        {
            ValidationText.Text = "Este evento não expõe outcomes negociáveis no momento.";
            SimulateButton.IsEnabled = false;
            return;
        }

        if (!IsVisible)
        {
            MarketStreamStatus.Text = "Mercado selecionado · abra o terminal para conectar";
            return;
        }

        _bookLoadCancellation?.Cancel();
        _bookLoadCancellation?.Dispose();
        _bookLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _bookLoadCancellation.Token;
        SimulateButton.IsEnabled = false;
        try
        {
            var bookTasks = _outcomes
                .Where(outcome => !string.IsNullOrWhiteSpace(outcome.TokenId))
                .Select(async outcome => (outcome.TokenId, Book: await _apiClient.GetOrderBookAsync(
                    outcome.TokenId,
                    cancellationToken)))
                .ToArray();
            var books = await Task.WhenAll(bookTasks);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals(
                    (MarketSelector.SelectedItem as MarketCard)?.ConditionId,
                    market.ConditionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            foreach (var (tokenId, book) in books)
            {
                _liveBooks[tokenId] = book;
            }

            if (OutcomeSelector.SelectedItem is MarketOutcome selectedOutcome
                && _liveBooks.TryGetValue(selectedOutcome.TokenId, out var selectedBook))
            {
                ApplyOrderBook(selectedOutcome.TokenId, selectedBook);
            }

            var subscribedTokens = _outcomes
                .Select(outcome => outcome.TokenId)
                .Where(tokenId => !string.IsNullOrWhiteSpace(tokenId))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            StartMarketStream(market.ConditionId, subscribedTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                MarketStreamStatus.Text = $"Falha ao carregar o book: {exception.Message}";
                MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(242, 140, 140));
                ValidationText.Text = $"Não foi possível carregar o book: {exception.Message}";
            }
        }
    }

    private async void OutcomeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible
            && !_isRefreshingOutcomes
            && OutcomeSelector.SelectedItem is MarketOutcome outcome)
        {
            _hasUserEditedPrice = false;
            if (_liveBooks.TryGetValue(outcome.TokenId, out var book))
            {
                ApplyOrderBook(outcome.TokenId, book);
            }
            else if (MarketSelector.SelectedItem is MarketCard market)
            {
                await LoadOrderBookAsync(market, outcome);
            }
        }
    }

    private async void PaperTradingPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RefreshPositions();
            if (MarketSelector.SelectedItem is MarketCard market
                && (_marketStreamCancellation is null
                    || !string.Equals(_activeMarketId, market.ConditionId, StringComparison.OrdinalIgnoreCase)))
            {
                await OpenMarketAsync(market);
            }
        }
        else
        {
            StopMarketStream();
            MarketStreamStatus.Text = "Feed pausado · terminal inativo";
            MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(98, 106, 122));
        }
    }

    private void StartMarketStream(string marketId, IReadOnlyList<string> assetIds)
    {
        if (assetIds.Count == 0)
        {
            MarketStreamStatus.Text = "Book carregado · sem outcomes para acompanhar";
            return;
        }

        _marketStreamCancellation = new CancellationTokenSource();
        var cancellationToken = _marketStreamCancellation.Token;
        MarketStreamStatus.Text = "Conectando ao feed em tempo real…";
        MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(232, 184, 103));
        _marketCountdownTimer.Start();
        _ = RunMarketStreamAsync(marketId, assetIds, cancellationToken);
    }

    private async Task RunMarketStreamAsync(
        string marketId,
        IReadOnlyList<string> assetIds,
        CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.StreamOrderBooksAsync(
                assetIds,
                async (assetId, orderBook) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested
                            && string.Equals(_activeMarketId, marketId, StringComparison.OrdinalIgnoreCase))
                        {
                            _pendingLiveBooks[assetId] = orderBook;
                            if (!_liveBookUpdateTimer.IsEnabled)
                            {
                                _liveBookUpdateTimer.Start();
                            }
                        }
                    });
                },
                status => Dispatcher.BeginInvoke(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !string.Equals(_activeMarketId, marketId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    MarketStreamStatus.Text = status ?? "Feed pausado";
                    MarketStreamIndicator.Fill = status?.StartsWith(
                        "Conectado",
                        StringComparison.OrdinalIgnoreCase) == true
                        ? new SolidColorBrush(Color.FromRgb(100, 226, 162))
                        : new SolidColorBrush(Color.FromRgb(232, 184, 103));
                }),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is WebSocketException or HttpRequestException or IOException or JsonException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested
                    && string.Equals(_activeMarketId, marketId, StringComparison.OrdinalIgnoreCase))
                {
                    MarketStreamStatus.Text = $"Feed indisponível: {exception.Message}";
                    MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(242, 140, 140));
                }
            });
        }
    }

    private void ApplyPendingBookUpdates()
    {
        _liveBookUpdateTimer.Stop();
        if (_pendingLiveBooks.Count == 0)
        {
            return;
        }

        foreach (var update in _pendingLiveBooks)
        {
            _liveBooks[update.Key] = update.Value;
        }
        _pendingLiveBooks.Clear();

        if (OutcomeSelector.SelectedItem is MarketOutcome outcome
            && _liveBooks.TryGetValue(outcome.TokenId, out var selectedBook))
        {
            ApplyOrderBook(outcome.TokenId, selectedBook);
        }
    }

    private void ApplyOrderBook(string assetId, MarketOrderBook orderBook)
    {
        _currentOrderBook = orderBook;
        _loadedOrderBookTokenId = assetId;
        UpdateOrderBookLabels(orderBook);
        SimulateButton.IsEnabled = true;
        ValidationText.Text = "";
        if (!_hasUserEditedPrice)
        {
            SetDefaultLimitPrice();
        }
    }

    private void StopMarketStream()
    {
        _marketStreamCancellation?.Cancel();
        _marketStreamCancellation?.Dispose();
        _marketStreamCancellation = null;
        _bookLoadCancellation?.Cancel();
        _bookLoadCancellation?.Dispose();
        _bookLoadCancellation = null;
        _liveBookUpdateTimer.Stop();
        _marketCountdownTimer.Stop();
        _pendingLiveBooks.Clear();
    }

    private void UpdateMarketCountdown()
    {
        if (MarketSelector.SelectedItem is not MarketCard market)
        {
            SelectedMarketTimer.Text = "";
            return;
        }

        if (market.EndDate is not { } endDate)
        {
            SelectedMarketTimer.Text = market.TimeframeLabel;
            return;
        }

        var remaining = endDate.ToLocalTime() - DateTime.Now;
        SelectedMarketTimer.Text = remaining > TimeSpan.Zero
            ? $"Encerra em {FormatRemainingTime(remaining)}"
            : "Aguardando resolução";
    }

    private async Task LoadOrderBookAsync(MarketCard market, MarketOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.TokenId))
        {
            ValidationText.Text = "Este outcome não possui token público para consultar o book.";
            SimulateButton.IsEnabled = false;
            return;
        }

        _bookLoadCancellation?.Cancel();
        _bookLoadCancellation?.Dispose();
        _bookLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _bookLoadCancellation.Token;
        SimulateButton.IsEnabled = false;
        ValidationText.Text = "Carregando book público…";
        try
        {
            var orderBook = await _apiClient.GetOrderBookAsync(outcome.TokenId, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals(
                    (MarketSelector.SelectedItem as MarketCard)?.ConditionId,
                    market.ConditionId,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    (OutcomeSelector.SelectedItem as MarketOutcome)?.TokenId,
                    outcome.TokenId,
                    StringComparison.Ordinal))
            {
                return;
            }

            _currentOrderBook = orderBook;
            _liveBooks[outcome.TokenId] = orderBook;
            ApplyOrderBook(outcome.TokenId, orderBook);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            _currentOrderBook = null;
            _loadedOrderBookTokenId = null;
            UpdateOrderBookLabels(null);
            ValidationText.Text = $"Não foi possível carregar o book: {exception.Message}";
        }
    }

    private void UpdateOrderBookLabels(MarketOrderBook? orderBook)
    {
        BestBidText.Text = orderBook is { BestBid: > 0m } ? orderBook.BestBid.ToString("P2") : "—";
        BestAskText.Text = orderBook is { BestAsk: > 0m } ? orderBook.BestAsk.ToString("P2") : "—";
        SpreadText.Text = orderBook?.SpreadLabel ?? "—";
        BidDepthText.Text = orderBook is null
            ? "Sem dados"
            : string.Join(Environment.NewLine, orderBook.Bids
                .OrderByDescending(level => level.Price)
                .Take(5)
                .Select(level => $"{level.Price:P2}  {level.Shares:N0}"));
        AskDepthText.Text = orderBook is null
            ? "Sem dados"
            : string.Join(Environment.NewLine, orderBook.Asks
                .OrderBy(level => level.Price)
                .Take(5)
                .Select(level => $"{level.Price:P2}  {level.Shares:N0}"));
    }

    private void SetDefaultLimitPrice()
    {
        if (_currentOrderBook is null)
        {
            return;
        }

        var isBuy = ((SideSelector.SelectedItem as ComboBoxItem)?.Content as string) != "SELL";
        var defaultPrice = isBuy ? _currentOrderBook.BestAsk : _currentOrderBook.BestBid;
        if (defaultPrice > 0m)
        {
            _hasUserEditedPrice = false;
            _isSettingDefaultPrice = true;
            try
            {
                PriceInput.Text = defaultPrice.ToString("0.####", CultureInfo.InvariantCulture);
            }
            finally
            {
                _isSettingDefaultPrice = false;
            }
        }

        UpdateOrderEstimate();
    }

    private void SideSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentOrderBook is not null)
        {
            SetDefaultLimitPrice();
        }
        else
        {
            UpdateOrderEstimate();
        }

        if (SplitCheckBox is null)
        {
            return;
        }

        SplitCheckBox.IsEnabled = ((SideSelector.SelectedItem as ComboBoxItem)?.Content as string) == "BUY";
        if (!SplitCheckBox.IsEnabled)
        {
            SplitCheckBox.IsChecked = false;
        }
    }

    private void OrderInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateOrderEstimate();

    private void PriceInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isSettingDefaultPrice)
        {
            _hasUserEditedPrice = true;
        }

        UpdateOrderEstimate();
    }

    private void UpdateOrderEstimate()
    {
        if (SharesInput is null || PriceInput is null || OrderEstimateText is null)
        {
            return;
        }

        if (!decimal.TryParse(SharesInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
            || !decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || shares <= 0m
            || price <= 0m)
        {
            OrderEstimateText.Text = "Informe shares e preço limite para estimar a ordem.";
            return;
        }

        var side = ((SideSelector.SelectedItem as ComboBoxItem)?.Content as string) ?? "BUY";
        OrderEstimateText.Text = side == "BUY"
            ? $"Máximo da ordem: ${(shares * price):N2} · limite por trade: ${_account.MaxTradeValue:N2}"
            : $"Valor bruto estimado: ${(shares * price):N2} · venda limitada às shares disponíveis";
    }

    private void MarketSearchInput_TextChanged(object sender, TextChangedEventArgs e) =>
        RestartSearchDebounce();

    private void RestartSearchDebounce()
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void MarketFilterSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarketSelector is not null)
        {
            RebuildMarketList((MarketSelector.SelectedItem as MarketCard)?.ConditionId);
        }
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "";
        if (MarketSelector.SelectedItem is not MarketCard market
            || OutcomeSelector.SelectedItem is not MarketOutcome outcome
            || !decimal.TryParse(SharesInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
            || !decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            ValidationText.Text = "Selecione um mercado e informe shares e preço limite válidos.";
            return;
        }

        if (!market.IsActive
            || market.IsClosed
            || market.EndDate is { } endDate && endDate <= DateTime.UtcNow)
        {
            ValidationText.Text = "Este mercado encerrou e não aceita novas ordens simuladas.";
            return;
        }

        try
        {
            PaperOrder order;
            if (SplitCheckBox.IsChecked == true)
            {
                order = _service.SimulateSplit(_account, ToPaperMarket(market), shares * price);
            }
            else
            {
                if (_currentOrderBook is null)
                {
                    ValidationText.Text = "O book público ainda não foi carregado.";
                    return;
                }

                var orderType = ((OrderTypeSelector.SelectedItem as ComboBoxItem)?.Content as string) ?? "FOK";
                var side = ((SideSelector.SelectedItem as ComboBoxItem)?.Content as string) ?? "BUY";
                order = _service.Simulate(_account, new PaperOrderRequest
                {
                    MarketId = market.MarketId,
                    ConditionId = market.ConditionId,
                    MarketTitle = market.Title,
                    TokenId = outcome.TokenId,
                    Outcome = outcome.Name,
                    Side = side,
                    OrderType = orderType,
                    Shares = shares,
                    Price = price
                }, _currentOrderBook);
            }

            RefreshCollections();
            AccountChanged?.Invoke(this, _account);
            ValidationText.Text = order.Status == "Filled"
                ? $"Ordem simulada com sucesso: {order.StatusLabel}"
                : $"Ordem registrada: {order.StatusLabel}";
        }
        catch (InvalidDataException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string orderId }
            || !_service.CancelOrder(_account, orderId))
        {
            return;
        }

        RefreshCollections();
        AccountChanged?.Invoke(this, _account);
        ValidationText.Text = "Ordem GTC cancelada; o saldo reservado foi liberado.";
    }

    private static PaperMarket ToPaperMarket(MarketCard market) => new()
    {
        Id = market.MarketId,
        ConditionId = market.ConditionId,
        Title = market.Title,
        Slug = market.Slug,
        ImageUrl = market.ImageUrl,
        Volume24h = market.Volume24h,
        EndDate = market.EndDate,
        IsClosed = market.IsClosed,
        Outcomes = market.Outcomes
    };

    private void RefreshCollections()
    {
        _orders.Clear();
        foreach (var order in _account.Orders)
        {
            _orders.Add(order);
        }

        RefreshPositions();
        OrdersEmptyState.Visibility = _orders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshSummary();
    }

    private void RefreshPositions()
    {
        _positions.Clear();
        foreach (var position in _account.Positions)
        {
            _positions.Add(position);
        }

        PositionsEmptyState.Visibility = _positions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshSummary()
    {
        var openPositions = _account.Positions.Where(position => position.Status == "Open").ToArray();
        BalanceText.Text = $"${_account.Balance:N2}";
        PositionValueText.Text = $"${openPositions.Sum(position => position.MarketValue):N2}";
        UnrealizedPnlText.Text = $"{openPositions.Sum(position => position.UnrealizedPnl):+$#,0.00;-$#,0.00;$0.00}";
        RealizedPnlText.Text = $"{_account.RealizedPnl:+$#,0.00;-$#,0.00;$0.00}";
        UnrealizedPnlText.Foreground = GetPnlBrush(openPositions.Sum(position => position.UnrealizedPnl));
        RealizedPnlText.Foreground = GetPnlBrush(_account.RealizedPnl);
    }

    private static Brush GetPnlBrush(decimal pnl) =>
        pnl < 0m
            ? new SolidColorBrush(Color.FromRgb(242, 140, 140))
            : new SolidColorBrush(Color.FromRgb(100, 226, 162));

    private static string FormatRemainingTime(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s";
}
