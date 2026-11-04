using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WalletCopy.Controls;

public partial class NotificationHost : UserControl
{
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(4.5) };

    public NotificationHost()
    {
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => Dismiss();
    }

    public void ShowSuccess(string message) => Show("Sucesso", message, "#64E2A2", "#20362D", "✓");

    public void ShowWarning(string message) => Show("Atenção", message, "#E8B867", "#382F20", "!");

    public void ShowError(string message) => Show("Não foi possível concluir", message, "#F28C8C", "#392528", "×");

    public void ShowInformation(string message) => Show("Informação", message, "#8497FF", "#252C43", "i");

    private void Show(string title, string message, string accent, string iconBackground, string icon)
    {
        TitleText.Text = title;
        MessageText.Text = message;
        SeverityBar.Background = CreateBrush(accent);
        IconCircle.Background = CreateBrush(iconBackground);
        IconText.Text = icon;
        ToastCard.Visibility = Visibility.Visible;
        ToastCard.Opacity = 1;
        _dismissTimer.Stop();
        _dismissTimer.Start();
    }

    private void Dismiss()
    {
        _dismissTimer.Stop();
        ToastCard.Opacity = 0;
        ToastCard.Visibility = Visibility.Collapsed;
    }

    private static SolidColorBrush CreateBrush(string color) => new((Color)ColorConverter.ConvertFromString(color)!);
}
