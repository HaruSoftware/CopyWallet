using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WalletCopy.Models;
using WalletCopy.Services;

namespace WalletCopy.Pages;

public partial class SettingsPage : UserControl
{
    private static readonly Regex AddressPattern = new("^0x[a-fA-F0-9]{40}$", RegexOptions.Compiled);
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(100, 226, 162));
    private static readonly SolidColorBrush DisconnectedBrush = new(Color.FromRgb(242, 140, 140));
    private static readonly SolidColorBrush PendingBrush = new(Color.FromRgb(232, 184, 103));

    private readonly PolymarketClobClient _clobClient = new();
    private readonly TradingCredentialService _credentialService = new();
    private TradingCredentials? _currentCredentials;
    private bool _isInitializing;

    public event EventHandler<string>? ThemeChanged;
    public event EventHandler<TradingCredentials?>? CredentialsChanged;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public void SetTheme(string preference)
    {
        _isInitializing = true;
        ThemeSelector.SelectedValue = preference;
        _isInitializing = false;
    }

    public void SetTradingCredentials(TradingCredentials? credentials)
    {
        _currentCredentials = credentials;
        UpdateStatusUi();
    }

    private void UpdateStatusUi()
    {
        if (_currentCredentials is not null && _currentCredentials.IsConfigured)
        {
            StatusDot.Fill = ConnectedBrush;
            StatusTitle.Text = "Conectado · Polymarket CLOB Ativo";
            var typeName = _currentCredentials.WalletType switch
            {
                PolymarketWalletType.PolymarketProxy => "Polymarket Proxy",
                PolymarketWalletType.GnosisSafe => "Gnosis Safe",
                _ => "EOA Direto"
            };
            StatusDetails.Text = $"Conta: {Shorten(_currentCredentials.EffectiveFunderAddress)} ({typeName}) · Signer: {Shorten(_currentCredentials.SignerAddress)}";
            DisconnectButton.Visibility = Visibility.Visible;
            ConnectButton.Content = "Atualizar Credenciais";
            CredentialsErrorText.Text = "";
        }
        else
        {
            StatusDot.Fill = DisconnectedBrush;
            StatusTitle.Text = "Desconectado · Nenhuma carteira vinculada";
            StatusDetails.Text = "Informe sua chave privada para habilitar a execução real de trades.";
            DisconnectButton.Visibility = Visibility.Collapsed;
            ConnectButton.Content = "Conectar e Gerar Chaves CLOB";
        }
    }

    private void WalletTypeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || WalletTypeSelector.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        var isEoa = tag == "Eoa";
        FunderAddressPanel.Visibility = isEoa ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        CredentialsErrorText.Text = "";
        var privateKey = PrivateKeyBox.Password.Trim();

        if (string.IsNullOrWhiteSpace(privateKey))
        {
            if (_currentCredentials is null || !_currentCredentials.HasPrivateKey)
            {
                CredentialsErrorText.Text = "Informe a chave privada exportada da sua conta Polymarket.";
                return;
            }

            privateKey = _currentCredentials.SignerPrivateKey;
        }

        var cleanKey = privateKey.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? privateKey[2..]
            : privateKey;

        if (cleanKey.Length != 64)
        {
            CredentialsErrorText.Text = "A chave privada deve possuir 64 caracteres hexadecimais (32 bytes).";
            return;
        }

        string signerAddress;
        try
        {
            signerAddress = PolymarketClobSigner.GetAddressFromPrivateKey(privateKey);
        }
        catch (Exception ex)
        {
            CredentialsErrorText.Text = $"Chave privada inválida: {ex.Message}";
            return;
        }

        var walletTypeTag = (WalletTypeSelector.SelectedValue as string) ?? "PolymarketProxy";
        var walletType = walletTypeTag switch
        {
            "Eoa" => PolymarketWalletType.Eoa,
            "GnosisSafe" => PolymarketWalletType.GnosisSafe,
            _ => PolymarketWalletType.PolymarketProxy
        };

        var funderAddress = FunderAddressBox.Text.Trim();
        if (walletType != PolymarketWalletType.Eoa)
        {
            if (string.IsNullOrWhiteSpace(funderAddress))
            {
                CredentialsErrorText.Text = "Para contas Proxy/Safe, informe o endereço da conta exibido no seu perfil da Polymarket.";
                return;
            }

            if (!AddressPattern.IsMatch(funderAddress))
            {
                CredentialsErrorText.Text = "Endereço da conta Polymarket inválido (deve começar com 0x seguido de 40 caracteres).";
                return;
            }
        }
        else
        {
            funderAddress = signerAddress;
        }

        ConnectButton.IsEnabled = false;
        StatusDot.Fill = PendingBrush;
        StatusTitle.Text = "Autenticando no CLOB da Polymarket…";
        StatusDetails.Text = "Assinando EIP-712 ClobAuth e gerando/derivando credenciais L2…";

        try
        {
            var apiCreds = await _clobClient.CreateOrDeriveApiCredentialsAsync(privateKey);

            var credentials = new TradingCredentials
            {
                SignerPrivateKey = privateKey,
                SignerAddress = signerAddress,
                FunderAddress = funderAddress,
                WalletType = walletType,
                ApiKey = apiCreds.ApiKey,
                ApiSecret = apiCreds.Secret,
                ApiPassphrase = apiCreds.Passphrase,
                ConfiguredAt = DateTime.UtcNow
            };

            await _credentialService.SaveCredentialsAsync(credentials);
            _currentCredentials = credentials;
            PrivateKeyBox.Clear();
            UpdateStatusUi();

            CredentialsChanged?.Invoke(this, credentials);
        }
        catch (Exception ex)
        {
            StatusDot.Fill = DisconnectedBrush;
            StatusTitle.Text = "Falha na conexão";
            StatusDetails.Text = ex.Message;
            CredentialsErrorText.Text = $"Erro de conexão: {ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Deseja desconectar sua conta de trading e remover as credenciais salvas localmente?",
            "Desconectar Polymarket",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _credentialService.ClearCredentials();
        _currentCredentials = null;
        PrivateKeyBox.Clear();
        FunderAddressBox.Clear();
        UpdateStatusUi();

        CredentialsChanged?.Invoke(this, null);
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string preference })
        {
            return;
        }

        ThemeChanged?.Invoke(this, preference);
    }

    private static string Shorten(string address) =>
        string.IsNullOrWhiteSpace(address) || address.Length < 10
            ? address
            : $"{address[..6]}…{address[^4..]}";
}
