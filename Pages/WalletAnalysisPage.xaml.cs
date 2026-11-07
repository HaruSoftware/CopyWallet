using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class WalletAnalysisPage : UserControl
{
    private readonly ObservableCollection<WalletTrade> _trades = [];

    public event EventHandler<string>? AnalysisRequested;

    public WalletAnalysisPage()
    {
        InitializeComponent();
        TradeList.ItemsSource = _trades;
    }

    public void SetWallets(IEnumerable<string> addresses, string selectedAddress)
    {
        var currentAddresses = addresses
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        WalletPicker.ItemsSource = currentAddresses;
        WalletPicker.SelectedItem = currentAddresses.FirstOrDefault(address =>
            address.Equals(selectedAddress, StringComparison.OrdinalIgnoreCase));
    }

    public void SetLoading(string address)
    {
        AnalyzeButton.IsEnabled = false;
        AnalysisPanel.Visibility = Visibility.Collapsed;
        _trades.Clear();
        TradesEmptyText.Text = "Carregando trades recentes…";
        TradesEmptyText.Visibility = Visibility.Visible;
        StatusText.Text = $"Consultando {address}…";
    }

    public void SetAnalysis(string address, IReadOnlyList<WalletTrade> trades)
    {
        AnalyzeButton.IsEnabled = true;
        AddressInput.Text = address;
        var orderedTrades = trades
            .Where(trade => trade.Timestamp != default)
            .OrderByDescending(trade => trade.Timestamp)
            .Take(30)
            .ToArray();
        foreach (var trade in orderedTrades)
        {
            _trades.Add(trade);
        }

        var analysis = WalletTradeAnalysis.Create(orderedTrades);
        TradeWindowText.Text = analysis.TradeWindowLabel;
        GrossBuyAmountText.Text = analysis.GrossBuyAmountLabel;
        RealizedResultText.Text = analysis.RealizedResultLabel;
        MatchedInvestmentText.Text = analysis.MatchedInvestmentLabel;
        AverageResultText.Text = analysis.AverageResultPerMatchedSaleLabel;
        ReturnOnInvestmentText.Text = analysis.ReturnOnMatchedInvestmentLabel;
        PeriodText.Text = analysis.PeriodLabel;
        ProfitabilityStatusText.Text = analysis.ResultLabel;
        ProfitabilitySummaryText.Text = analysis.UnmatchedSaleCount > 0
            ? $"{analysis.ProfitabilitySummary} Vendas sem par nesta amostra: {analysis.UnmatchedSaleCount}."
            : analysis.ProfitabilitySummary;
        ProfileText.Text = analysis.Profile;
        SummaryText.Text = analysis.Summary;
        EvidenceText.Text = analysis.Evidence;
        TradeCountText.Text = analysis.TradeCountLabel;
        MarketCountText.Text = analysis.MarketCountLabel;
        AverageTradeValueText.Text = analysis.AverageTradeValueLabel;
        TakerPercentageText.Text = analysis.TakerPercentageLabel;
        var resultColor = !analysis.HasRealizedResult
            ? "ThemeMuted"
            : analysis.RealizedResult > 0m
                ? "ThemeAccent"
                : analysis.RealizedResult < 0m
                    ? "ThemeError"
                    : "ThemeSecondary";
        RealizedResultText.SetResourceReference(TextBlock.ForegroundProperty, resultColor);
        ReturnOnInvestmentText.SetResourceReference(TextBlock.ForegroundProperty, resultColor);
        ProfitabilityStatusText.SetResourceReference(TextBlock.ForegroundProperty, resultColor);
        AnalysisPanel.Visibility = Visibility.Visible;
        TradesEmptyText.Text = orderedTrades.Length == 0
            ? "Nenhum trade recente encontrado para esta carteira."
            : "";
        TradesEmptyText.Visibility = orderedTrades.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = $"{orderedTrades.Length} trades recentes carregados para {address}.";
    }

    public void SetError(string message)
    {
        AnalyzeButton.IsEnabled = true;
        AnalysisPanel.Visibility = Visibility.Collapsed;
        _trades.Clear();
        TradesEmptyText.Text = "Não foi possível carregar os trades.";
        TradesEmptyText.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void WalletPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WalletPicker.SelectedItem is string address)
        {
            AddressInput.Text = address;
        }
    }

    private void Analyze_Click(object sender, RoutedEventArgs e) => RequestAnalysis();

    private void AddressInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RequestAnalysis();
        }
    }

    private void RequestAnalysis()
    {
        AnalysisRequested?.Invoke(this, AddressInput.Text.Trim());
    }
}
