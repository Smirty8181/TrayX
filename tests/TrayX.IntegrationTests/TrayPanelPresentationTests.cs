using System.Windows.Media;
using TrayX.Contracts;
using TrayX.Windows.Shell;

namespace TrayX.IntegrationTests;

public sealed class TrayPanelPresentationTests
{
    [Fact]
    public void SelectIconsForMonitor_OnlyReturnsVisibleIconsFromRequestedMonitorInTrayOrder()
    {
        var icons = new[]
        {
            CreateIcon("secondary-right", 1, true, 200),
            CreateIcon("primary", 0, true, 50),
            CreateIcon("hidden", 1, false, 10),
            CreateIcon("secondary-left", 1, true, 100)
        };

        var result = TrayPanelPresentation.SelectIconsForMonitor(icons, 1);

        Assert.Equal(new[] { "secondary-left", "secondary-right" }, result.Select(icon => icon.Id));
    }

    [Theory]
    [InlineData("dark", "light", 245, 20, 25, 43)]
    [InlineData("light", "dark", 250, 255, 255, 255)]
    [InlineData("system", "dark", 245, 20, 25, 43)]
    [InlineData("system", "light", 250, 255, 255, 255)]
    public void CreateTheme_RespectsThemeModeAndConfiguredAccent(
        string themeMode,
        string systemMode,
        byte surfaceAlpha,
        byte surfaceRed,
        byte surfaceGreen,
        byte surfaceBlue)
    {
        var result = TrayPanelPresentation.CreateTheme(themeMode, "#22D3EE", systemMode);

        Assert.Equal(Color.FromArgb(surfaceAlpha, surfaceRed, surfaceGreen, surfaceBlue), result.Surface);
        Assert.Equal(Color.FromRgb(0x22, 0xD3, 0xEE), result.Accent);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    public void CanApplyIconImage_RequiresSourceAndLiveDispatcher(
        bool hasSource,
        bool shutdownStarted,
        bool shutdownFinished,
        bool expected)
    {
        var result = TrayPanelPresentation.CanApplyIconImage(
            hasSource ? new object() : null,
            shutdownStarted,
            shutdownFinished);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, 22)]
    [InlineData(96, 22)]
    [InlineData(144, 33)]
    [InlineData(192, 44)]
    [InlineData(768, 176)]
    [InlineData(2000, 256)]
    public void GetIconDecodePixelWidth_ScalesForEffectiveDpi(uint dpi, int expected)
    {
        Assert.Equal(expected, TrayPanelPresentation.GetIconDecodePixelWidth(dpi));
    }

    [Theory]
    [InlineData(0, 0, 1920, 1080, true)]
    [InlineData(-1920, 0, 0, 1080, true)]
    [InlineData(0, 0, 1918, 1080, true)]
    [InlineData(0, 0, 1917, 1080, false)]
    [InlineData(0, 0, 1800, 1000, false)]
    public void CoversDisplay_OnlyMatchesForegroundWindowsFillingDisplay(
        int displayX,
        int displayY,
        int windowRight,
        int windowBottom,
        bool expected)
    {
        var result = TrayPanelPresentation.CoversDisplay(
            displayX,
            displayY,
            1920,
            1080,
            displayX,
            displayY,
            windowRight,
            windowBottom);

        Assert.Equal(expected, result);
    }

    private static TrayLiveIconDto CreateIcon(string id, int monitorIndex, bool onBar, int left) =>
        new(
            id,
            id,
            "application",
            monitorIndex,
            onBar,
            true,
            false,
            true,
            1,
            null,
            null,
            null,
            left,
            0,
            16,
            16);
}
