using System.Globalization;
using System.Windows;
using WalletCopy.Models;

namespace WalletCopy;

public partial class CopyTradeDialog : Window
{
    private readonly WalletTrade _trade;

    public CopyTradeDraft? Draft { get; private set; }

    public CopyTradeDialog(WalletTrade trade)
    {
        InitializeComponent();
        _trade = trade;
        MarketText.Text = $"{trade.MarketTitle}\n{trade.WalletLabel}";
        AutomaticSummary.Text = $"{trade.SideLabel} · {trade.OutcomePriceLabel} · {trade.SharesLabel} · {trade.AmountLabel}";
        SizeInput.Text = trade.Size.ToString("0.####", CultureInfo.InvariantCulture);
        PriceInput.Text = trade.Price.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        var manual = ManualMode.IsChecked == true;
        AutomaticPanel.Visibility = manual ? Visibility.Collapsed : Visibility.Visible;
        ManualPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (AutomaticMode.IsChecked == true)
        {
            Draft = new CopyTradeDraft
            {
                Mode = "Automatico",
                Position = _trade.Outcome,
                Size = _trade.Size,
                Price = _trade.Price
            };
            DialogResult = true;
            return;
        }

        if (!decimal.TryParse(SizeInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var size)
            || size <= 0m)
        {
            ValidationText.Text = "Informe um tamanho de posição maior que zero.";
            return;
        }

        if (!decimal.TryParse(PriceInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price)
            || price <= 0m || price >= 1m)
        {
            ValidationText.Text = "Informe um preço maior que 0 e menor que 1.";
            return;
        }

        Draft = new CopyTradeDraft
        {
            Mode = "Manual",
            Position = UpPosition.IsChecked == true ? "Up" : "Down",
            Size = size,
            Price = price
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
