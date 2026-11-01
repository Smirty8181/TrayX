using TrayX.Windows.Shell;

namespace TrayX.IntegrationTests;

public sealed class TrayAppWindowCatalogIntegrationTests
{
    [Theory]
    [InlineData(false, false, 0L, false)]
    [InlineData(true, true, 0L, false)]
    [InlineData(true, false, 0x80L, false)]
    [InlineData(true, false, 0L, true)]
    public void IsManageableWindow_RequiresVisibleUnownedNonToolWindow(
        bool isVisible,
        bool hasOwner,
        long extendedStyle,
        bool expected)
    {
        Assert.Equal(expected, TrayAppWindowCatalog.IsManageableWindow(isVisible, hasOwner, extendedStyle));
    }

    [Fact]
    public void GetVisibleWindows_ReturnsOnlyIdentifiableVisibleWindows()
    {
        var windows = new TrayAppWindowCatalog().GetVisibleWindows();

        Assert.All(windows, window =>
        {
            Assert.NotEqual(0, window.Handle);
            Assert.True(window.ProcessId > 0);
            Assert.False(string.IsNullOrWhiteSpace(window.ProcessName));
            Assert.False(string.IsNullOrWhiteSpace(window.Title));
            Assert.False(string.IsNullOrWhiteSpace(window.WindowClass));
        });
        Assert.DoesNotContain(windows, window => window.ProcessId == Environment.ProcessId);
    }
}
