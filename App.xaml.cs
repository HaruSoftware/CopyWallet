using System.Windows;
using System.Windows.Media;
using ModernWpf;

namespace WalletCopy;

public partial class App : System.Windows.Application
{
    public static void ApplyTheme(FrameworkElement target, string? preference)
    {
        var elementTheme = preference switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        ThemeManager.SetRequestedTheme(target, elementTheme);
        var isLight = ThemeManager.GetActualTheme(target) == ElementTheme.Light;
        SetBrush("ThemeBackground", isLight ? "#F7F8FA" : "#0B0D12");
        SetBrush("ThemeSidebar", isLight ? "#FFFFFF" : "#0E1118");
        SetBrush("ThemePanel", isLight ? "#FFFFFF" : "#11141C");
        SetBrush("ThemePanelRaised", isLight ? "#F0F2F5" : "#171B25");
        SetBrush("ThemeBorder", isLight ? "#D9DDE5" : "#242936");
        SetBrush("ThemeBorderStrong", isLight ? "#C7CDD8" : "#303747");
        SetBrush("ThemeText", isLight ? "#18202B" : "#F4F5F7");
        SetBrush("ThemeSecondary", isLight ? "#5E6878" : "#8C93A3");
        SetBrush("ThemeMuted", isLight ? "#667181" : "#A1A8B6");
        SetBrush("ThemeNav", isLight ? "#4D5868" : "#AAB0BD");
        SetBrush("ThemeLabel", isLight ? "#667181" : "#687081");
        SetBrush("ThemeDim", isLight ? "#7B8492" : "#626A7A");
        SetBrush("ThemeControl", isLight ? "#E9ECF1" : "#1B212C");
        SetBrush("ThemeSuccessBackground", isLight ? "#E1F4E9" : "#20362D");
        SetBrush("ThemeErrorBackground", isLight ? "#FBE7E7" : "#421B1B");
        SetBrush("ThemeAccentText", isLight ? "#123D26" : "#092015");
    }

    private static void SetBrush(string key, string color)
    {
        Current.Resources[key] = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(color));
    }
}
