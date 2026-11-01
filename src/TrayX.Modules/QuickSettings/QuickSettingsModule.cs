using System.Diagnostics;
using TrayX.Application.Modules;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Display;
using TrayX.Windows.Themes;

namespace TrayX.Modules.QuickSettings;

public sealed class QuickSettingsModule : ITrayXModule
{
    private static readonly IReadOnlyDictionary<string, string> SettingsPages =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["sound"] = "ms-settings:sound",
            ["apps-volume"] = "ms-settings:apps-volume",
            ["display"] = "ms-settings:display",
            ["personalization"] = "ms-settings:personalization",
            ["startup"] = "ms-settings:startupapps",
            ["bluetooth"] = "ms-settings:bluetooth",
            ["wifi"] = "ms-settings:network-wifi",
            ["airplane"] = "ms-settings:network-airplanemode",
            ["microphone"] = "ms-settings:privacy-microphone",
            ["camera"] = "ms-settings:privacy-webcam",
            ["power"] = "ms-settings:powersleep",
            ["nightlight"] = "ms-settings:nightlight",
            ["powersaver"] = "ms-settings:powersleep",
            ["focus"] = "ms-settings:notifications",
            ["privacy"] = "ms-settings:privacy",
            ["notifications"] = "ms-settings:notifications",
            ["date-time"] = "ms-settings:dateandtime",
            ["taskbar"] = "ms-settings:taskbar",
            ["apps"] = "ms-settings:appsfeatures"
        };

    private readonly WindowsThemeService _themes;

    public QuickSettingsModule(WindowsThemeService themes)
    {
        _themes = themes;
    }

    public string Id => "quick";

    public string DisplayName => "Быстрые настройки";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public WindowsThemeDto GetWindowsTheme() => new(_themes.GetMode());

    public WindowsThemeDto SetWindowsTheme(string mode)
    {
        if (string.Equals(mode, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            _themes.Toggle();
        }
        else if (string.Equals(mode, "light", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase))
        {
            _themes.SetMode(mode.ToLowerInvariant());
        }
        else
        {
            throw new ArgumentException("Тема должна быть light, dark или toggle.", nameof(mode));
        }

        return GetWindowsTheme();
    }

    public void OpenSettingsPage(string page)
    {
        if (!SettingsPages.TryGetValue(page, out var uri))
        {
            throw new ArgumentException("Неизвестная страница параметров Windows.", nameof(page));
        }

        using var process = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    public void OpenDisplaySettings() => DisplayService.OpenWindowsDisplaySettings();

    public void OpenClassicSoundControlPanel()
    {
        using var process = Process.Start(new ProcessStartInfo("mmsys.cpl") { UseShellExecute = true });
    }
}
