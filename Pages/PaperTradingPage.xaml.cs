using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WalletCopy.Models;
using WalletCopy.Services;

namespace WalletCopy.Pages;

public partial class PaperTradingPage : UserControl
{
    private readonly ObservableCollection<MarketCard> _markets = [];
    private readonly ObservableCollection<PaperOrder> _orders = [];
    private readonly PaperTradingService _service = new();
    private PaperAccount _account = new();

    public event EventHandler<PaperAccount>? AccountChanged;

    public PaperTradingPage()
    {
        InitializeComponent();
        MarketSelector.ItemsSource = _markets;
        OutcomeSelector.ItemsSource = new ObservableCollection<MarketOutcome>();
        OrderList.ItemsSource = _orders;
    }

    public void SetMarkets(IEnumerable<MarketCard> markets)
    {
        _markets.Clear();
        foreach (var market in markets)
        {
            _markets.Add(market);
        }
        if (_markets.Count > 0 && MarketSelector.SelectedIndex < 0)
        {
            MarketSelector.SelectedIndex = 0;
        }
    }

    public void SetAccount(PaperAccount account)
    {
        _account = account;
        _orders.Clear();
        foreach (var order in account.Orders)
        {
            _orders.Add(order);
        }
        RefreshSummary();
    }

    public PaperAccount GetAccount() => _account;

    private void MarketSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarketSelector.SelectedItem is not MarketCard market)
        {
            return;
        }

        SelectedMarketText.Text = $"{market.Title}\n{market.VolumeLabel}";
        var outcomes = (ObservableCollection<MarketOutcome>)OutcomeSelector.ItemsSource;
        outcomes.Clear();
        foreach (var outcome in market.Outcomes)
        {
            outcomes.Add(outcome);
        }
        if (outcomes.Count == 0)
        {
            outcomes.Add(new MarketOutcome { Name = "Yes", Price = 0.5m });
            outcomes.Add(new MarketOutcome { Name = "No", Price = 0.5m });
        }
        OutcomeSelector.SelectedIndex = 0;
        PriceInput.Text = outcomes[0].Price > 0m
            ? outcomes[0].Price.ToString("0.####", CultureInfo.InvariantCulture)
            : "0.50";
    }

    private void Simulate_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = "";
        if (MarketSelector.SelectedItem is not MarketCard market
            || OutcomeSelector.SelectedItem is not MarketOutcome outcome
            || !decimal.TryParse(SharesInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var shares)
            || !decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || !decimal.TryParse(SlippageInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var slippage))
        {
            ValidationText.Text = "Selecione um mercado e informe valores numéricos válidos.";
            return;
        }

        try
        {
            var orderType = ((OrderTypeSelector.SelectedItem as ComboBoxItem)?.Content as string) ?? "FOK";
            var order = SplitCheckBox.IsChecked == true
                ? _service.SimulateSplit(_account, ToPaperMarket(market), shares * price)
                : _service.Simulate(_account, new PaperOrderRequest
                {
                    MarketId = market.MarketId,
                    MarketTitle = market.Title,
                    TokenId = outcome.TokenId,
                    Outcome = outcome.Name,
                    OrderType = orderType,
                    Shares = shares,
                    Price = price,
                    MaxSlippagePercent = slippage
                });
            _orders.Insert(0, order);
            RefreshSummary();
            AccountChanged?.Invoke(this, _account);
            ValidationText.Text = $"Ordem simulada: {order.StatusLabel}";
        }
        catch (InvalidDataException exception)
        {
            ValidationText.Text = exception.Message;
        }
    }

    private static PaperMarket ToPaperMarket(MarketCard market) => new()
    {
        Id = market.MarketId,
        Title = market.Title,
        Slug = market.Slug,
        ImageUrl = market.ImageUrl,
        Volume24h = market.Volume24h,
        Outcomes = market.Outcomes
    };

    private void RefreshSummary()
    {
        BalanceText.Text = $"${_account.Balance:N2}";
        ExposureText.Text = $"${_account.Orders.Sum(order => order.TotalCost):N2}";
    }
}
