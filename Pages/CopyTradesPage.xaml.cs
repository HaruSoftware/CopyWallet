using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows.Media;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class CopyTradesPage : UserControl
{
    private static readonly SolidColorBrush ConnectedBrush = new(Color.FromRgb(100, 226, 162));
    private static readonly SolidColorBrush MutedBrush = new(Color.FromRgb(160, 160, 160));

    private readonly ObservableCollection<CopyTradeRecord> _records = [];

    public CopyTradesPage()
    {
        InitializeComponent();
        CopiedTradesList.ItemsSource = _records;
    }

    public void SetRecords(IEnumerable<CopyTradeRecord> records)
    {
        _records.Clear();
        foreach (var record in records)
        {
            _records.Add(record);
        }

        EmptyState.Visibility = _records.Count == 0
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    public void UpdateConnectionStatus(bool isConnected)
    {
        if (isConnected)
        {
            ExecutionIndicatorDot.Fill = ConnectedBrush;
            ExecutionIndicatorText.Text = "EXECUÇÃO POLYMARKET CLOB ATIVA";
        }
        else
        {
            ExecutionIndicatorDot.Fill = MutedBrush;
            ExecutionIndicatorText.Text = "CONECTE SUA CONTA NAS CONFIGURAÇÕES PARA ENVIAR ORDENS REAIS";
        }
    }
}
