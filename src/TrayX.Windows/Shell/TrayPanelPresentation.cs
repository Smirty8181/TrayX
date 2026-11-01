using TrayX.Contracts;
using System.Windows.Media;

namespace TrayX.Windows.Shell;

internal static class TrayPanelPresentation
{
    public static TrayPanelTheme CreateTheme(string? themeMode, string? accent, string? systemThemeMode)
    {
        var isDark = themeMode switch
        {
            "light" => false,
            "system" => string.Equals(systemThemeMode, "dark", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
        var normalizedAccent = TrayXSettings.NormalizeAccent(accent);
        var accentColor = (Color)ColorConverter.ConvertFromString(normalizedAccent);

        return isDark
            ? new TrayPanelTheme(
                Color.FromArgb(245, 20, 25, 43),
                Color.FromArgb(230, 82, 91, 125),
                Color.FromRgb(243, 246, 255),
                Color.FromRgb(140, 150, 179),
                accentColor,
                Color.FromArgb(48, accentColor.R, accentColor.G, accentColor.B))
            : new TrayPanelTheme(
                Color.FromArgb(250, 255, 255, 255),
                Color.FromArgb(230, 220, 227, 242),
                Color.FromRgb(16, 24, 40),
                Color.FromRgb(102, 112, 133),
                accentColor,
                Color.FromArgb(35, accentColor.R, accentColor.G, accentColor.B));
    }

    public static TrayLiveIconDto[] SelectIconsForMonitor(
        IEnumerable<TrayLiveIconDto> icons,
        int monitorIndex) =>
        icons
            .Where(icon => icon.OnBar && icon.MonitorIndex == monitorIndex)
            .OrderBy(icon => icon.Left)
            .ToArray();

    public static bool CanApplyIconImage(
        object? source,
        bool dispatcherHasShutdownStarted,
        bool dispatcherHasShutdownFinished) =>
        source is not null &&
        !dispatcherHasShutdownStarted &&
        !dispatcherHasShutdownFinished;

    public static int GetIconDecodePixelWidth(uint dpiX)
    {
        var effectiveDpi = dpiX > 0 ? dpiX : 96u;
        return Math.Clamp((int)Math.Ceiling(22.0 * effectiveDpi / 96.0), 22, 256);
    }

    public static bool CoversDisplay(
        int displayX,
        int displayY,
        int displayWidth,
        int displayHeight,
        int windowLeft,
        int windowTop,
        int windowRight,
        int windowBottom)
    {
        const int edgeTolerance = 2;
        if (displayWidth <= 0 || displayHeight <= 0)
        {
            return false;
        }

        var displayRight = (long)displayX + displayWidth;
        var displayBottom = (long)displayY + displayHeight;
        return Math.Abs((long)windowLeft - displayX) <= edgeTolerance &&
            Math.Abs((long)windowTop - displayY) <= edgeTolerance &&
            Math.Abs((long)windowRight - displayRight) <= edgeTolerance &&
            Math.Abs((long)windowBottom - displayBottom) <= edgeTolerance;
    }
}

internal sealed record TrayPanelTheme(
    Color Surface,
    Color Border,
    Color Text,
    Color MutedText,
    Color Accent,
    Color AccentSurface);
