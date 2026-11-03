using System.Collections.ObjectModel;
using System.Windows.Controls;
using WalletCopy.Models;

namespace WalletCopy.Pages;

public partial class CopyTradesPage : UserControl
{
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
}
