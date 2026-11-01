using System.Windows.Media;

namespace TrayX.UI.Services;

internal static class AccentContrast
{
    public static Color GetOnAccentTextColor(Color accent)
    {
        var luminance = RelativeLuminance(accent);
        var blackContrast = (luminance + 0.05) / 0.05;
        var whiteContrast = 1.05 / (luminance + 0.05);
        return whiteContrast >= blackContrast ? Colors.White : Colors.Black;
    }

    private static double RelativeLuminance(Color color)
    {
        var red = Linearize(color.R / 255d);
        var green = Linearize(color.G / 255d);
        var blue = Linearize(color.B / 255d);
        return 0.2126 * red + 0.7152 * green + 0.0722 * blue;
    }

    private static double Linearize(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
}
