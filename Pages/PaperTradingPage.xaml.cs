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
    private readonly ObservableCollection<PaperOrder> _visibleOrders = [];
    private readonly ObservableCollection<PaperPosition> _positions = [];
    private readonly PaperTradingService _service = new();
    private readonly PolymarketApiClient _apiClient;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _liveBookUpdateTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _marketCountdownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, MarketOrderBook> _liveBooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketOrderBook> _pendingLiveBooks = new(StringComparer.Ordinal);
    private readonly object _pendingBookLock = new();
    private PaperAccount _account = new();
    private MarketOrderBook? _currentOrderBook;
    private string? _currentOrderBookTokenId;
    private CancellationTokenSource? _bookLoadCancellation;
    private CancellationTokenSource? _marketStreamCancellation;
    private string? _activeMarketId;
    private string _activeTimeframeFilter = "5m";
    private string _activeOrderFilter = "Todos";
    private bool _isRefreshingOutcomes;
    private bool _isSettingDefaultPrice;
    private bool _hasUserEditedPrice;
    private bool _bookUpdateQueued;
    private decimal? _lastDisplayedUpPrice;
    private decimal? _lastDisplayedDownPrice;
    private IReadOnlyList<MarketCard>? _marketSnapshot;

    public event EventHandler<PaperAccount>? AccountChanged;

    public PaperTradingPage(PolymarketApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponent();
        MarketSelector.ItemsSource = _visibleMarkets;
        OutcomeSelector.ItemsSource = _outcomes;
        OrderList.ItemsSource = _visibleOrders;
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
        _marketSnapshot = markets as IReadOnlyList<MarketCard> ?? nextMarkets;

        var selectedMarketId = (MarketSelector.SelectedItem as MarketCard)?.ConditionId;
        _markets.Clear();
        foreach (var market in nextMarkets)
        {
            _markets.Add(market);
        }

        RebuildMarketList(selectedMarketId);
        RefreshPositions();
        RefreshSummary();
    }

    public void SelectMarket(string conditionId)
    {
        _searchDebounce.Stop();
        MarketSearchInput.Text = "";
        _activeTimeframeFilter = "Todos";
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

    public void SimulateSplitDirect(PaperMarket market, decimal amount)
    {
        _service.SimulateSplit(_account, market, amount);
        RefreshCollections();
        AccountChanged?.Invoke(this, _account);
    }

    private void TimeframeFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _activeTimeframeFilter = tag;
            RebuildMarketList((MarketSelector.SelectedItem as MarketCard)?.ConditionId);
        }
    }

    private void RebuildMarketList(string? selectedMarketId = null)
    {
        var query = MarketSearchInput.Text.Trim();
        var now = DateTime.UtcNow;

        var filteredMarkets = _markets.Where(market =>
        {
            if (market.IsClosed || (market.EndDate.HasValue && market.EndDate.Value <= now.AddSeconds(-30)))
            {
                return false;
            }

            if (_activeTimeframeFilter != "Todos")
            {
                if (!string.Equals(market.TimeframeLabel, _activeTimeframeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (query.Length > 0)
            {
                return market.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || market.EventTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || market.Asset.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || market.Slug.Contains(query, StringComparison.OrdinalIgnoreCase);
            }

            return true;
        });

        // Garantir no máximo uma rodada ativa por (Asset, Timeframe) para eliminar rodadas antigas ou futuras
        var deduplicated = filteredMarkets
            .GroupBy(m => string.IsNullOrEmpty(m.TimeframeLabel) ? m.ConditionId : $"{m.Asset}_{m.TimeframeLabel}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(m => m.EndDate ?? DateTime.MaxValue).First())
            .OrderBy(m => m.TimeframeLabel switch { "5m" => 1, "15m" => 2, "1h" => 3, "4h" => 4, _ => 5 })
            .ThenBy(m => m.Asset);

        _visibleMarkets.Clear();
        foreach (var market in deduplicated)
        {
            _visibleMarkets.Add(market);
        }

        MarketCountBadge.Text = $"{_visibleMarkets.Count} ativos";

        if (!string.IsNullOrWhiteSpace(selectedMarketId))
        {
            MarketSelector.SelectedItem = _visibleMarkets.FirstOrDefault(market =>
                market.ConditionId.Equals(selectedMarketId, StringComparison.OrdinalIgnoreCase));
        }

        if (MarketSelector.SelectedItem is null && _visibleMarkets.Count > 0)
        {
            MarketSelector.SelectedIndex = 0;
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
        _lastDisplayedUpPrice = null;
        _lastDisplayedDownPrice = null;
        _currentOrderBook = null;
        _currentOrderBookTokenId = null;
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
            UpdateOutcomeButtonsUi();
        }
        finally
        {
            _isRefreshingOutcomes = false;
        }

        UpdateOrderBookLabels(null);
        if (_outcomes.Count == 0)
        {
            ValidationText.Text = "Este evento não expõe outcomes negociáveis no momento.";
            SimulateButton.IsEnabled = false;
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
                .Select(async outcome => (outcome.TokenId, Book: await _apiClient.GetOrderBookAsync(outcome.TokenId, cancellationToken)))
                .ToArray();
            var books = await Task.WhenAll(bookTasks);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals((MarketSelector.SelectedItem as MarketCard)?.ConditionId, market.ConditionId, StringComparison.OrdinalIgnoreCase))
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

            UpdateOutcomePricesFromBooks();

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
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                MarketStreamStatus.Text = $"Falha ao carregar book: {exception.Message}";
                MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(242, 140, 140));
                ValidationText.Text = $"Não foi possível carregar o book: {exception.Message}";
            }
        }
    }

    private void UpOutcomeCard_Click(object sender, RoutedEventArgs e)
    {
        if (_outcomes.Count > 0)
        {
            OutcomeSelector.SelectedIndex = 0;
            UpdateOutcomeButtonsUi();
            OnOutcomeSelected();
        }
    }

    private void DownOutcomeCard_Click(object sender, RoutedEventArgs e)
    {
        if (_outcomes.Count > 1)
        {
            OutcomeSelector.SelectedIndex = 1;
            UpdateOutcomeButtonsUi();
            OnOutcomeSelected();
        }
    }

    private void UpdateOutcomeButtonsUi()
    {
        var isUpSelected = OutcomeSelector.SelectedIndex == 0;
        var accentBrush = (Brush)FindResource("ThemeAccent");
        var errorBrush = (Brush)FindResource("ThemeError");
        var borderBrush = (Brush)FindResource("ThemeBorder");

        if (UpOutcomeCard is not null)
        {
            UpOutcomeCard.BorderBrush = isUpSelected ? accentBrush : borderBrush;
            UpOutcomeCard.BorderThickness = new Thickness(isUpSelected ? 2 : 1);
        }

        if (DownOutcomeCard is not null)
        {
            DownOutcomeCard.BorderBrush = !isUpSelected ? errorBrush : borderBrush;
            DownOutcomeCard.BorderThickness = new Thickness(!isUpSelected ? 2 : 1);
        }

        if (_outcomes.Count > 0)
        {
            UpOutcomeLabel.Text = _outcomes[0].Name.ToUpperInvariant();
        }
        if (_outcomes.Count > 1)
        {
            DownOutcomeLabel.Text = _outcomes[1].Name.ToUpperInvariant();
        }

        UpdateActionExecuteButtonText();
    }

    private void OnOutcomeSelected()
    {
        if (OutcomeSelector.SelectedItem is MarketOutcome outcome)
        {
            _hasUserEditedPrice = false;
            if (_liveBooks.TryGetValue(outcome.TokenId, out var book))
            {
                ApplyOrderBook(outcome.TokenId, book);
            }
            else if (MarketSelector.SelectedItem is MarketCard market)
            {
                _ = LoadOrderBookAsync(market, outcome);
            }
        }
    }

    private void OutcomeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsVisible && !_isRefreshingOutcomes)
        {
            UpdateOutcomeButtonsUi();
            OnOutcomeSelected();
        }
    }

    private void Side_Checked(object sender, RoutedEventArgs e)
    {
        if (_currentOrderBook is not null)
        {
            SetDefaultLimitPrice();
        }
        else
        {
            UpdateOrderEstimate();
        }
        UpdateActionExecuteButtonText();
    }

    private void OrderTypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateOrderEstimate();
    }

    private void OrderFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string tag })
        {
            _activeOrderFilter = tag;
            if (OrdersCountText is not null)
            {
                RefreshOrders();
            }
        }
    }

    private void QuickAmount_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag })
        {
            if (tag == "MAX")
            {
                var maxAmount = Math.Min(_account.Balance, _account.MaxTradeValue);
                AmountUsdInput.Text = Math.Max(0m, maxAmount).ToString("0.00", CultureInfo.InvariantCulture);
            }
            else if (decimal.TryParse(tag, NumberStyles.Number, CultureInfo.InvariantCulture, out var increment))
            {
                var current = decimal.TryParse(AmountUsdInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;
                var next = Math.Min(_account.MaxTradeValue, current + increment);
                AmountUsdInput.Text = next.ToString("0.00", CultureInfo.InvariantCulture);
            }
        }
    }

    private void AmountUsdInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateOrderEstimate();
    }

    private void PriceInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isSettingDefaultPrice)
        {
            _hasUserEditedPrice = true;
        }
        UpdateOrderEstimate();
    }

    private void OrderInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateOrderEstimate();

    private void SplitCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOrderEstimate();
        UpdateActionExecuteButtonText();
    }

    private void UpdateActionExecuteButtonText()
    {
        if (SimulateButton is null) return;
        var isBuy = SideBuyRadio?.IsChecked == true;
        var actionWord = isBuy ? "Comprar" : "Vender";
        var isSplit = SplitCheckBox?.IsChecked == true;

        if (isSplit)
        {
            SimulateButton.Content = $"Simular Test Split · ${AmountUsdInput?.Text ?? "50.00"}";
            return;
        }

        var outcomeName = (OutcomeSelector?.SelectedItem as MarketOutcome)?.Name ?? "UP";
        SimulateButton.Content = $"{actionWord} {outcomeName} · ${AmountUsdInput?.Text ?? "50.00"}";
    }

    private void UpdateOrderEstimate()
    {
        if (AmountUsdInput is null || PriceInput is null || EstimatedSharesText is null || PotentialReturnText is null || ProjectedProfitText is null)
        {
            return;
        }

        if (!decimal.TryParse(AmountUsdInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amountUsd)
            || !decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || amountUsd <= 0m
            || price <= 0m)
        {
            EstimatedSharesText.Text = "—";
            PotentialReturnText.Text = "—";
            ProjectedProfitText.Text = "—";
            UpdateActionExecuteButtonText();
            return;
        }

        var shares = Math.Round(amountUsd / price, 2);
        SharesInput.Text = shares.ToString("0.##", CultureInfo.InvariantCulture);
        EstimatedSharesText.Text = $"{shares:N2} shares";

        var potentialPayout = shares * 1.00m; // polymarket pays $1.00 per share if outcome wins
        PotentialReturnText.Text = $"${potentialPayout:N2} ($1.00/sh)";

        var estimatedProfit = potentialPayout - amountUsd;
        var profitPercent = amountUsd > 0m ? (estimatedProfit / amountUsd) * 100m : 0m;

        ProjectedProfitText.Text = $"{estimatedProfit:+$#,0.00;-$#,0.00;$0.00} ({profitPercent:+0.0;-0.0;0.0}%)";
        ProjectedProfitText.Foreground = estimatedProfit >= 0m
            ? (Brush)FindResource("ThemeAccent")
            : (Brush)FindResource("ThemeError");

        UpdateActionExecuteButtonText();
    }

    private void ApplyPendingBookUpdates()
    {
        _liveBookUpdateTimer.Stop();
        Dictionary<string, MarketOrderBook> updates;
        lock (_pendingBookLock)
        {
            _bookUpdateQueued = false;
            if (_pendingLiveBooks.Count == 0)
            {
                return;
            }

            updates = new Dictionary<string, MarketOrderBook>(_pendingLiveBooks, StringComparer.Ordinal);
            _pendingLiveBooks.Clear();
        }

        foreach (var update in updates)
        {
            _liveBooks[update.Key] = update.Value;
        }

        UpdateOutcomePricesFromBooks();

        if (OutcomeSelector.SelectedItem is MarketOutcome outcome
            && _liveBooks.TryGetValue(outcome.TokenId, out var selectedBook))
        {
            ApplyOrderBook(outcome.TokenId, selectedBook);
        }
    }

    private void UpdateOutcomePricesFromBooks()
    {
        if (_outcomes.Count > 0 && _liveBooks.TryGetValue(_outcomes[0].TokenId, out var upBook))
        {
            var upPrice = upBook.BestAsk > 0m ? upBook.BestAsk : (upBook.BestBid > 0m ? upBook.BestBid : _outcomes[0].Price);
            if (upPrice > 0m && _lastDisplayedUpPrice != upPrice)
            {
                _lastDisplayedUpPrice = upPrice;
                UpPriceText.Text = $"{Math.Round(upPrice * 100m):0}¢";
                var multiple = Math.Round(((1.00m - upPrice) / upPrice) * 100m, 1);
                UpReturnMultiple.Text = $"+{multiple}%";
            }
        }

        if (_outcomes.Count > 1 && _liveBooks.TryGetValue(_outcomes[1].TokenId, out var downBook))
        {
            var downPrice = downBook.BestAsk > 0m ? downBook.BestAsk : (downBook.BestBid > 0m ? downBook.BestBid : _outcomes[1].Price);
            if (downPrice > 0m && _lastDisplayedDownPrice != downPrice)
            {
                _lastDisplayedDownPrice = downPrice;
                DownPriceText.Text = $"{Math.Round(downPrice * 100m):0}¢";
                var multiple = Math.Round(((1.00m - downPrice) / downPrice) * 100m, 1);
                DownReturnMultiple.Text = $"+{multiple}%";
            }
        }
    }

    private void ApplyOrderBook(string assetId, MarketOrderBook orderBook)
    {
        var bookChanged = _currentOrderBook is null
            || !string.Equals(_currentOrderBookTokenId, assetId, StringComparison.Ordinal)
            || _currentOrderBook.BestBid != orderBook.BestBid
            || _currentOrderBook.BestAsk != orderBook.BestAsk;
        _currentOrderBook = orderBook;
        _currentOrderBookTokenId = assetId;
        if (!bookChanged)
        {
            return;
        }

        UpdateOrderBookLabels(orderBook);
        SimulateButton.IsEnabled = true;
        ValidationText.Text = "";
        if (!_hasUserEditedPrice)
        {
            SetDefaultLimitPrice();
        }
    }

    private void SetDefaultLimitPrice()
    {
        if (_currentOrderBook is null) return;
        var isBuy = SideBuyRadio.IsChecked == true;
        var defaultPrice = isBuy ? _currentOrderBook.BestAsk : _currentOrderBook.BestBid;
        if (defaultPrice <= 0m)
        {
            defaultPrice = isBuy ? (_currentOrderBook.BestBid > 0m ? _currentOrderBook.BestBid : 0.50m) : 0.50m;
        }

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

    private void UpdateOrderBookLabels(MarketOrderBook? orderBook)
    {
        BestBidText.Text = orderBook is { BestBid: > 0m } ? orderBook.BestBid.ToString("P1") : "—";
        BestAskText.Text = orderBook is { BestAsk: > 0m } ? orderBook.BestAsk.ToString("P1") : "—";
        SpreadText.Text = orderBook?.SpreadLabel ?? "—";
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
            ? $"⏱ Encerra em {FormatRemainingTime(remaining)}"
            : "Aguardando resolução";
    }

    private void StartMarketStream(string marketId, IReadOnlyList<string> assetIds)
    {
        if (assetIds.Count == 0) return;
        _marketStreamCancellation = new CancellationTokenSource();
        var cancellationToken = _marketStreamCancellation.Token;
        MarketStreamStatus.Text = "Conectando ao feed em tempo real…";
        MarketStreamIndicator.Fill = new SolidColorBrush(Color.FromRgb(232, 184, 103));
        _marketCountdownTimer.Start();
        _ = RunMarketStreamAsync(marketId, assetIds, cancellationToken);
    }

    private async Task RunMarketStreamAsync(string marketId, IReadOnlyList<string> assetIds, CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.StreamOrderBooksAsync(
                assetIds,
                async (assetId, orderBook) =>
                {
                    var shouldQueue = false;
                    lock (_pendingBookLock)
                    {
                        if (!cancellationToken.IsCancellationRequested
                            && string.Equals(_activeMarketId, marketId, StringComparison.OrdinalIgnoreCase))
                        {
                            _pendingLiveBooks[assetId] = orderBook;
                            shouldQueue = !_bookUpdateQueued;
                            _bookUpdateQueued = true;
                        }
                    }

                    if (shouldQueue)
                    {
                        await Dispatcher.BeginInvoke(
                            DispatcherPriority.Background,
                            new Action(() =>
                            {
                                if (!_liveBookUpdateTimer.IsEnabled)
                                {
                                    _liveBookUpdateTimer.Start();
                                }
                            }));
                    }
                },
                status => Dispatcher.BeginInvoke(() =>
                {
                    if (cancellationToken.IsCancellationRequested
                        || !string.Equals(_activeMarketId, marketId, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    MarketStreamStatus.Text = status ?? "Feed pausado";
                    MarketStreamIndicator.Fill = status?.StartsWith("Conectado", StringComparison.OrdinalIgnoreCase) == true
                        ? new SolidColorBrush(Color.FromRgb(100, 226, 162))
                        : new SolidColorBrush(Color.FromRgb(232, 184, 103));
                }),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is WebSocketException or HttpRequestException or IOException or JsonException)
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
        lock (_pendingBookLock)
        {
            _bookUpdateQueued = false;
            _pendingLiveBooks.Clear();
        }
    }

    private async Task LoadOrderBookAsync(MarketCard market, MarketOutcome outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome.TokenId)) return;
        _bookLoadCancellation?.Cancel();
        _bookLoadCancellation?.Dispose();
        _bookLoadCancellation = new CancellationTokenSource();
        var cancellationToken = _bookLoadCancellation.Token;
        SimulateButton.IsEnabled = false;

        try
        {
            var orderBook = await _apiClient.GetOrderBookAsync(outcome.TokenId, cancellationToken);
            if (cancellationToken.IsCancellationRequested
                || !string.Equals((MarketSelector.SelectedItem as MarketCard)?.ConditionId, market.ConditionId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _currentOrderBook = orderBook;
            _liveBooks[outcome.TokenId] = orderBook;
            ApplyOrderBook(outcome.TokenId, orderBook);
            UpdateOutcomePricesFromBooks();
        }
        catch { }
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "";
        if (MarketSelector.SelectedItem is not MarketCard market
            || OutcomeSelector.SelectedItem is not MarketOutcome outcome
            || !decimal.TryParse(SharesInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
            || !decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price))
        {
            ValidationText.Text = "Selecione um mercado e informe um valor válido.";
            return;
        }

        if (!market.IsActive || market.IsClosed || (market.EndDate.HasValue && market.EndDate.Value <= DateTime.UtcNow))
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
                    ValidationText.Text = "Aguarde o carregamento do book público da Polymarket.";
                    return;
                }

                var side = SideBuyRadio.IsChecked == true ? "BUY" : "SELL";
                var orderType = OrderTypeSelector.SelectedIndex switch
                {
                    1 => "FAK",
                    2 => "GTC",
                    _ => "FOK"
                };

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
            ValidationText.Text = $"Ordem simulada com sucesso! Status: {order.StatusLabel}";
        }
        catch (InvalidDataException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private void CloseFull_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PaperPosition position })
        {
            try
            {
                _liveBooks.TryGetValue(position.TokenId, out var book);
                _service.ClosePositionMarket(_account, position, position.Shares, book);
                RefreshCollections();
                AccountChanged?.Invoke(this, _account);
                ValidationText.Text = $"Posição fechada com sucesso a mercado.";
            }
            catch (Exception ex)
            {
                ValidationText.Text = $"Erro ao fechar posição: {ex.Message}";
            }
        }
    }

    private void ClosePartial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: PaperPosition position })
        {
            try
            {
                var partialShares = Math.Round(position.Shares / 2m, 2);
                _liveBooks.TryGetValue(position.TokenId, out var book);
                _service.ClosePositionMarket(_account, position, partialShares, book);
                RefreshCollections();
                AccountChanged?.Invoke(this, _account);
                ValidationText.Text = $"50% da posição fechada ({partialShares:N2} shares).";
            }
            catch (Exception ex)
            {
                ValidationText.Text = $"Erro ao fechar parcial: {ex.Message}";
            }
        }
    }

    private void ResetAccount_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            "Deseja resetar o saldo da conta simulada para $10.000,00 e limpar todas as posições?",
            "Resetar Conta Simulado",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _service.ResetAccount(_account, 10_000m);
            RefreshCollections();
            AccountChanged?.Invoke(this, _account);
            ValidationText.Text = "Conta virtual restaurada com saldo inicial de $10.000,00.";
        }
    }

    private void CancelOrder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string orderId } || !_service.CancelOrder(_account, orderId))
        {
            return;
        }

        RefreshCollections();
        AccountChanged?.Invoke(this, _account);
        ValidationText.Text = "Ordem GTC cancelada; saldo virtual liberado.";
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
        RefreshOrders();
        RefreshPositions();
        RefreshSummary();
    }

    private void RefreshOrders()
    {
        _orders.Clear();
        foreach (var order in _account.Orders)
        {
            _orders.Add(order);
        }

        _visibleOrders.Clear();
        foreach (var order in _orders.Where(order => _activeOrderFilter switch
        {
            "Abertas" => order.Status is "Open" or "Partial fill",
            "Executadas" => order.Status is "Filled" or "Cancelled",
            _ => true
        }))
        {
            _visibleOrders.Add(order);
        }

        OrdersEmptyState.Visibility = _visibleOrders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OrdersCountText.Text = _orders.Count.ToString(CultureInfo.InvariantCulture);
        OpenOrdersCountText.Text = _orders.Count(order => order.Status is "Open" or "Partial fill")
            .ToString(CultureInfo.InvariantCulture);
        ExecutedVolumeText.Text = $"${_orders.Sum(order => order.TotalCost):N2}";
        ReservedCashText.Text = $"${_orders.Sum(order => order.ReservedCash):N2}";
    }

    private void RefreshPositions()
    {
        _positions.Clear();
        foreach (var position in _account.Positions.Where(p => p.Status == "Open"))
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
        TotalPnlText.Text = $"{(_account.RealizedPnl + openPositions.Sum(position => position.UnrealizedPnl)):+$#,0.00;-$#,0.00;$0.00}";
        UnrealizedPnlText.Foreground = GetPnlBrush(openPositions.Sum(position => position.UnrealizedPnl));
        RealizedPnlText.Foreground = GetPnlBrush(_account.RealizedPnl);
        TotalPnlText.Foreground = GetPnlBrush(_account.RealizedPnl + openPositions.Sum(position => position.UnrealizedPnl));
    }

    private static Brush GetPnlBrush(decimal pnl) =>
        pnl < 0m
            ? new SolidColorBrush(Color.FromRgb(242, 140, 140))
            : new SolidColorBrush(Color.FromRgb(100, 226, 162));

    private static string FormatRemainingTime(TimeSpan remaining) =>
        remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m"
            : $"{remaining.Minutes:D2}m {remaining.Seconds:D2}s";

    private void MarketSearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
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
}
