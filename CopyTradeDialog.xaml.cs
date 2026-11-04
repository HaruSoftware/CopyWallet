using System.Globalization;
using System.Windows;
using WalletCopy.Models;

namespace WalletCopy;

public partial class CopyTradeDialog : Window
{
    private readonly WalletTrade _trade;
    private readonly bool _hasLiveCredentials;

    public CopyTradeDraft? Draft { get; private set; }

    public CopyTradeDialog(WalletTrade trade, bool hasLiveCredentials = false)
    {
        InitializeComponent();
        _trade = trade;
        _hasLiveCredentials = hasLiveCredentials;

        MarketText.Text = $"{trade.MarketTitle}\n{trade.WalletLabel}";
        AutomaticSummary.Text = $"{trade.SideLabel} · {trade.OutcomePriceLabel} · {trade.SharesLabel} · {trade.AmountLabel}";
        SizeInput.Text = trade.Size.ToString("0.####", CultureInfo.InvariantCulture);
        PriceInput.Text = trade.Price.ToString("0.####", CultureInfo.InvariantCulture);
        OrderTypeInput.SelectedIndex = 0;

        if (!hasLiveCredentials)
        {
            PaperExecutionMode.IsChecked = true;
            RealExecutionMode.IsEnabled = false;
            RealExecutionMode.ToolTip = "Configure suas credenciais na aba Configurações para habilitar a execução real.";
        }

        UpdateExecutionWarning();
    }

    private void ExecutionTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateExecutionWarning();
    }

    private void TestSplit_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        var isSplit = TestSplitCheckBox.IsChecked == true;
        TestSplitInfoText.Visibility = isSplit ? Visibility.Visible : Visibility.Collapsed;
        UpdateExecutionWarning();
    }

    private void UpdateExecutionWarning()
    {
        var isSplit = TestSplitCheckBox.IsChecked == true;

        if (RealExecutionMode.IsChecked == true)
        {
            if (!_hasLiveCredentials)
            {
                ExecutionWarningText.Text = "Aviso: Credenciais de trading não configuradas. Acesse a página Configurações para conectar sua conta Polymarket.";
                ValidationText.Text = "Necessário configurar credenciais antes de enviar ordens reais.";
                ConfirmButton.IsEnabled = false;
            }
            else
            {
                ExecutionWarningText.Text = isSplit
                    ? "🛡️ TEST SPLIT REAL: Serão enviadas ordens para ambos os lados (Up + Down) na Polymarket CLOB. Como 1 Up + 1 Down pagam $1.00 garantido na liquidação, seu risco de perda é zero."
                    : "Execução Real: Esta ordem será assinada com sua chave e enviada ao livro de ofertas da Polymarket (Polygon 137). Certifique-se de possuir saldo USDC.e ou pUSD.";
                ValidationText.Text = "";
                ConfirmButton.IsEnabled = true;
            }
        }
        else
        {
            ExecutionWarningText.Text = isSplit
                ? "Simulação Test Split: Ordem simulada em ambos os lados na conta virtual de Paper Trading."
                : "Modo Simulação: A ordem será registrada localmente no terminal Paper Trading, sem envolver fundos reais.";
            ValidationText.Text = "";
            ConfirmButton.IsEnabled = true;
        }
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
        var isReal = RealExecutionMode.IsChecked == true;
        var isSplit = TestSplitCheckBox.IsChecked == true;

        if (isReal && !_hasLiveCredentials)
        {
            ValidationText.Text = "Configure suas credenciais na aba Configurações antes de enviar ordens reais.";
            return;
        }

        if (AutomaticMode.IsChecked == true)
        {
            Draft = new CopyTradeDraft
            {
                Mode = "Automatico",
                ExecutionTarget = isReal ? "Real" : "Paper",
                Position = _trade.Outcome,
                Side = _trade.Side,
                OrderType = "FOK",
                TokenId = _trade.TokenId,
                ConditionId = _trade.ConditionId,
                Size = _trade.Size,
                Price = _trade.Price,
                MaxSlippagePercent = 1.5m,
                IsSplit = isSplit
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

        if (!decimal.TryParse(SlippageInput.Text, NumberStyles.Number, CultureInfo.InvariantCulture,
                out var slippage) || slippage < 0m || slippage > 100m)
        {
            ValidationText.Text = "Informe um slippage entre 0 e 100%.";
            return;
        }

        Draft = new CopyTradeDraft
        {
            Mode = "Manual",
            ExecutionTarget = isReal ? "Real" : "Paper",
            Position = UpPosition.IsChecked == true ? "Up" : "Down",
            Side = _trade.Side,
            OrderType = (OrderTypeInput.SelectedValue as string) ?? "FOK",
            TokenId = _trade.TokenId,
            ConditionId = _trade.ConditionId,
            Size = size,
            Price = price,
            MaxSlippagePercent = slippage,
            IsSplit = isSplit
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
