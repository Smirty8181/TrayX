using TrayX.Windows.Display;

namespace TrayX.IntegrationTests;

public sealed class DisplayServiceIntegrationTests
{
    [Fact]
    public void GetDisplays_ReturnsValidMonitorGeometryAndDpi()
    {
        var displays = new DisplayService().GetDisplays();

        Assert.NotEmpty(displays);
        Assert.Single(displays, display => display.IsPrimary);
        Assert.Equal(displays.Count, displays.Select(display => display.DeviceName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(displays, display => Assert.False(string.IsNullOrWhiteSpace(display.StableId)));
        Assert.Equal(displays.Count, displays.Select(display => display.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(displays, display =>
        {
            Assert.True(display.Width > 0 && display.Height > 0);
            Assert.True(display.WorkWidth > 0 && display.WorkHeight > 0);
            Assert.True(display.DpiX >= 96 && display.DpiY >= 96);
        });
    }
}
