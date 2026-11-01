using System.Windows.Media;
using System.Xml.Linq;
using TrayX.UI.Services;

namespace TrayX.IntegrationTests;

public sealed class ThemeContrastTests
{
    [Theory]
    [InlineData(0xFF, 0xC8, 0x57)]
    [InlineData(0x3A, 0x2D, 0x8F)]
    [InlineData(0x7C, 0x5C, 0xFF)]
    [InlineData(0xDD, 0xDD, 0xDD)]
    [InlineData(0x11, 0x11, 0x11)]
    public void OnAccentText_MeetsWcagAaForLightAndDarkAccents(byte red, byte green, byte blue)
    {
        var accent = Color.FromRgb(red, green, blue);
        var foreground = AccentContrast.GetOnAccentTextColor(accent);

        Assert.True(
            ContrastRatio(foreground, accent) >= 4.5,
            $"Contrast for #{red:X2}{green:X2}{blue:X2} was {ContrastRatio(foreground, accent):F2}:1.");
    }

    [Fact]
    public void LightThemeFaintText_MeetsWcagAaOnItsWindowBackground()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Themes", "Theme.Light.xaml");
        var document = XDocument.Load(path);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var faint = ReadColor(document, presentation, xaml, "TrayX.TextFaintColor");
        var background = ReadColor(document, presentation, xaml, "TrayX.BackgroundColor");

        Assert.True(
            ContrastRatio(faint, background) >= 4.5,
            $"Light-theme faint text contrast was {ContrastRatio(faint, background):F2}:1.");
    }

    private static Color ReadColor(XDocument document, XNamespace presentation, XNamespace xaml, string key)
    {
        var value = document.Descendants(presentation + "Color")
            .Single(element => (string?)element.Attribute(xaml + "Key") == key)
            .Value;
        return (Color)ColorConverter.ConvertFromString(value);
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        var lighter = Math.Max(firstLuminance, secondLuminance);
        var darker = Math.Min(firstLuminance, secondLuminance);
        return (lighter + 0.05) / (darker + 0.05);
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

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TrayX.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
