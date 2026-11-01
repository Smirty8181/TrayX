using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TrayX.Contracts;
using System.IO;

namespace TrayX.UI.Services;

public static class ThemeManager
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly Color GradientSecondary = Color.FromRgb(0xFF, 0x3F, 0x94);

    public static bool IsDark { get; private set; } = true;

    public static void Apply(TrayXSettings settings, bool windowsIsDark)
    {
        var application = Application.Current;
        if (application is null)
        {
            return;
        }

        var dark = settings.ThemeMode switch
        {
            "light" => false,
            "system" => windowsIsDark,
            _ => true
        };

        var themeSource = dark ? "Themes/Theme.Dark.xaml" : "Themes/Theme.Light.xaml";
        var dictionaries = application.Resources.MergedDictionaries;
        if (dictionaries.Count == 0 ||
            !string.Equals(
                dictionaries[0].Source?.OriginalString,
                themeSource,
                StringComparison.OrdinalIgnoreCase))
        {
            var theme = new ResourceDictionary
            {
                Source = new Uri(themeSource, UriKind.Relative)
            };

            if (dictionaries.Count == 0)
            {
                dictionaries.Add(theme);
            }
            else
            {
                dictionaries[0] = theme;
            }
        }

        ApplyAccent(application, settings.Accent);
        IsDark = dark;

        foreach (Window window in application.Windows)
        {
            ApplyWindowChrome(window, dark);
        }
    }

    public static void ApplyAccent(Application application, string? accentHex)
    {
        var accent = ParseColor(accentHex);
        var accentBrush = Freeze(new SolidColorBrush(accent));
        var soft = Freeze(new SolidColorBrush(Color.FromArgb(0x29, accent.R, accent.G, accent.B)));
        var nav = Freeze(new SolidColorBrush(Color.FromArgb(0x26, accent.R, accent.G, accent.B)));

        var gradient = new LinearGradientBrush(accent, GradientSecondary, new Point(0, 0), new Point(1, 1));
        gradient.Freeze();

        application.Resources["TrayX.AccentBrush"] = accentBrush;
        application.Resources["TrayX.OnAccentBrush"] = Freeze(
            new SolidColorBrush(AccentContrast.GetOnAccentTextColor(accent)));
        application.Resources["TrayX.AccentSoftBrush"] = soft;
        application.Resources["TrayX.NavSelectedBrush"] = nav;
        application.Resources["TrayX.AccentGradientBrush"] = gradient;
    }

    public static bool GetWindowsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int number)
            {
                return number == 0;
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }

        return true;
    }

    public static void ApplyWindowChrome(Window window, bool? dark = null)
    {
        if (!window.IsInitialized)
        {
            window.SourceInitialized += (_, _) => ApplyWindowChrome(window, dark);
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var useDark = (dark ?? IsDark) ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref useDark, sizeof(int));

        var corner = DwmcpRound;
        _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
    }

    private static Color ParseColor(string? value)
    {
        var normalized = TrayXSettings.NormalizeAccent(value);
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(normalized);
            return Color.FromRgb(color.R, color.G, color.B);
        }
        catch (FormatException)
        {
            return Color.FromRgb(0x7C, 0x5C, 0xFF);
        }
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
