using System.Windows;
using System.Windows.Controls;

namespace WalletCopy.Pages;

public partial class SettingsPage : UserControl
{
    private bool _isInitializing;

    public event EventHandler<string>? ThemeChanged;

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

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing || ThemeSelector.SelectedItem is not ComboBoxItem { Tag: string preference })
        {
            return;
        }

        ThemeChanged?.Invoke(this, preference);
    }
}
