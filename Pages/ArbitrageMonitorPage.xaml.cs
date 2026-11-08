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

public partial class ArbitrageMonitorPage : UserControl
{
    private readonly PolymarketApiClient _apiClient;
    private readonly ObservableCollection<MarketOpportunityQuote> _quotes = [];
    private readonly Dictionary<string, MarketOrderBook> _books = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketOrderBook> _pendingBooks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MarketFeeSchedule> _feeSchedules = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingBookLock = new();
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<MarketCard> _markets = [];
    private CancellationTokenSource? _monitorCancellation;
    private string[] _activeTokenIds = [];
    private string _timeframe = "5m";
    private decimal? _targetAmount = 1.01m;
    private bool _isVisible;
    private bool _isInitialized;

    public ArbitrageMonitorPage(PolymarketApiClient apiClient)
    {
        _apiClient = apiClient;
        InitializeComponent();
        TargetInput.Text = _targetAmount.GetValueOrDefault().ToString("0.00", CultureInfo.CurrentCulture);
        OpportunityList.ItemsSource = _quotes;
        _isInitialized = true;
        _updateTimer.Tick += (_, _) => ApplyPendingBooks();
        IsVisibleChanged += (_, args) =>
        {
            _isVisible = (bool)args.NewValue;
            if (_isVisible)
            {
                RestartMonitor();
            }
            else
            {
                StopMonitor();
            }
        };
    }

    public void SetMarkets(IReadOnlyList<MarketCard> markets)
    {
        _markets = markets;
        RefreshQuotes();
        if (_isVisible)
        {
            RestartMonitorIfMarketSetChanged();
        }
    }

    public void StopMonitoring() => StopMonitor();

    private void Timeframe_Checked(object sender, RoutedEventArgs e)
    {
        if (_isInitialized && sender is RadioButton { Tag: string timeframe })
        {
            _timeframe = timeframe;
            RefreshQuotes();
            if (_isVisible)
            {
                RestartMonitor();
            }
        }
    }

    private void TargetInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TargetValidation is null)
        {
            return;
        }

        var numberStyles = NumberStyles.AllowLeadingWhite
                           | NumberStyles.AllowTrailingWhite
                           | NumberStyles.AllowDecimalPoint;
        var validCurrentCulture = decimal.TryParse(
                                      TargetInput.Text,
                                      numberStyles,
                                      CultureInfo.CurrentCulture,
                                      out var target)
                                  && target > 0m
                                  && target <= 2m;
        var validInvariantCulture = !validCurrentCulture
                                    && decimal.TryParse(
                                        TargetInput.Text,
                                        numberStyles,
                                        CultureInfo.InvariantCulture,
                                        out target)
                                    && target > 0m
                                    && target <= 2m;
        _targetAmount = validCurrentCulture || validInvariantCulture ? target : null;
        TargetValidation.Text = _targetAmount.HasValue
            ? ""
            : "Informe uma meta maior que $0,00 e menor ou igual a $2,00.";
        TargetValidation.Visibility = _targetAmount.HasValue ? Visibility.Collapsed : Visibility.Visible;
        RefreshQuotes();
    }

    private void RestartMonitorIfMarketSetChanged()
    {
        var currentTokenIds = GetCurrentMarkets().SelectMany(market => market.Outcomes)
            .Select(outcome => outcome.TokenId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tokenId => tokenId, StringComparer.Ordinal);
        var monitoredTokenIds = _monitorCancellation?.Token.IsCancellationRequested == false
            ? _activeTokenIds
            : [];
        if (currentTokenIds.SequenceEqual(monitoredTokenIds, StringComparer.Ordinal))
        {
            return;
        }

        RestartMonitor();
    }

    private void RestartMonitor()
    {
        StopMonitor();
        var markets = GetCurrentMarkets();
        var tokenIds = markets.SelectMany(market => market.Outcomes)
            .Select(outcome => outcome.TokenId)
            .Where(tokenId => !string.IsNullOrWhiteSpace(tokenId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tokenId => tokenId, StringComparer.Ordinal)
            .ToArray();
        _activeTokenIds = tokenIds;
        _books.Clear();
        _feeSchedules.Clear();
        lock (_pendingBookLock)
        {
            _pendingBooks.Clear();
        }

        RefreshQuotes();
        if (tokenIds.Length == 0)
        {
            SetConnectionStatus("Nenhum mercado binário ativo encontrado para este período.", false);
            return;
        }

        _monitorCancellation = new CancellationTokenSource();
        var cancellationToken = _monitorCancellation.Token;
        _updateTimer.Start();
        _ = LoadInitialDataAsync(markets, cancellationToken);
        _ = RunMarketStreamAsync(tokenIds, cancellationToken);
    }

    private void StopMonitor()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _updateTimer.Stop();
        _activeTokenIds = [];
        lock (_pendingBookLock)
        {
            _pendingBooks.Clear();
        }
    }

    private async Task LoadInitialDataAsync(
        IReadOnlyList<MarketCard> markets,
        CancellationToken cancellationToken)
    {
        var feeTasks = markets.Select(async market =>
        {
            var feeSchedule = await _apiClient.GetMarketFeeScheduleAsync(market.Slug, cancellationToken);
            return (market.ConditionId, feeSchedule);
        }).ToArray();
        var bookTasks = markets.SelectMany(market => market.Outcomes.Select(async outcome =>
        {
            var book = await _apiClient.GetOrderBookAsync(outcome.TokenId, cancellationToken);
            return (outcome.TokenId, book);
        })).ToArray();

        var errors = new List<string>();
        foreach (var feeTask in feeTasks)
        {
            try
            {
                var (conditionId, feeSchedule) = await feeTask;
                if (!cancellationToken.IsCancellationRequested)
                {
                    _feeSchedules[conditionId] = feeSchedule;
                    if (!feeSchedule.IsAvailable && !string.IsNullOrWhiteSpace(feeSchedule.ErrorMessage))
                    {
                        errors.Add($"taxa {conditionId[..Math.Min(8, conditionId.Length)]}: {feeSchedule.ErrorMessage}");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"taxa: {exception.Message}");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or IOException or InvalidDataException)
            {
                errors.Add($"taxa: {exception.Message}");
            }
        }

        foreach (var bookTask in bookTasks)
        {
            try
            {
                var (tokenId, book) = await bookTask;
                if (!cancellationToken.IsCancellationRequested)
                {
                    QueueBookUpdate(tokenId, book);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                continue;
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                errors.Add($"book: {exception.Message}");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or JsonException or IOException or InvalidDataException)
            {
                errors.Add($"book: {exception.Message}");
            }
        }

        if (errors.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetConnectionStatus($"Dados parciais · {string.Join(" · ", errors.Take(2))}", false);
                RefreshQuotes();
            });
        }
    }

    private async Task RunMarketStreamAsync(string[] tokenIds, CancellationToken cancellationToken)
    {
        try
        {
            await _apiClient.StreamOrderBooksAsync(
                tokenIds,
                (tokenId, book) =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        QueueBookUpdate(tokenId, book);
                    }

                    return Task.CompletedTask;
                },
                status =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var isConnected = status?.StartsWith(
                        "Conectado",
                        StringComparison.OrdinalIgnoreCase) == true;
                    if (!isConnected)
                    {
                        lock (_pendingBookLock)
                        {
                            _pendingBooks.Clear();
                        }
                    }

                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            if (!isConnected)
                            {
                                _books.Clear();
                                RefreshQuotes();
                            }

                            SetConnectionStatus(status ?? "Feed pausado", isConnected);
                        }
                    });
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (
            exception is WebSocketException or HttpRequestException or IOException or JsonException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                await Dispatcher.InvokeAsync(() => SetConnectionStatus($"Feed indisponível: {exception.Message}", false));
            }
        }
    }

    private void QueueBookUpdate(string tokenId, MarketOrderBook book)
    {
        lock (_pendingBookLock)
        {
            _pendingBooks[tokenId] = book;
        }
    }

    private void ApplyPendingBooks()
    {
        lock (_pendingBookLock)
        {
            foreach (var (tokenId, book) in _pendingBooks)
            {
                _books[tokenId] = book;
            }

            _pendingBooks.Clear();
        }

        RefreshQuotes();
    }

    private void RefreshQuotes()
    {
        var quotes = GetCurrentMarkets()
            .Select(market => new MarketOpportunityQuote
            {
                Market = market,
                FirstOutcome = market.Outcomes[0],
                SecondOutcome = market.Outcomes[1],
                FirstBook = _books.GetValueOrDefault(market.Outcomes[0].TokenId),
                SecondBook = _books.GetValueOrDefault(market.Outcomes[1].TokenId),
                FeeSchedule = _feeSchedules.TryGetValue(market.ConditionId, out var feeSchedule)
                    ? feeSchedule
                    : null,
                TargetAmount = _targetAmount
            })
            .OrderByDescending(quote => _targetAmount.HasValue && quote.IsOpportunity)
            .ThenBy(quote => quote.Market.Asset, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        for (var index = 0; index < quotes.Length; index++)
        {
            if (index < _quotes.Count)
            {
                _quotes[index] = quotes[index];
            }
            else
            {
                _quotes.Add(quotes[index]);
            }
        }

        while (_quotes.Count > quotes.Length)
        {
            _quotes.RemoveAt(_quotes.Count - 1);
        }

        EmptyState.Visibility = quotes.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MarketCard[] GetCurrentMarkets() =>
        _markets
            .Where(market => market.TimeframeLabel.Equals(_timeframe, StringComparison.OrdinalIgnoreCase)
                             && market.IsActive
                             && !market.IsClosed
                             && (!market.EndDate.HasValue || market.EndDate.Value > DateTime.UtcNow)
                             && market.Outcomes.Count == 2
                             && market.Outcomes.All(outcome => !string.IsNullOrWhiteSpace(outcome.TokenId)))
            .GroupBy(market => market.ConditionId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(market => market.Asset, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private void SetConnectionStatus(string message, bool connected)
    {
        ConnectionStatus.Text = message;
        ConnectionIndicator.Fill = connected ? ConnectedBrush : PendingBrush;
    }

    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(100, 226, 162));
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(232, 184, 103));
}
