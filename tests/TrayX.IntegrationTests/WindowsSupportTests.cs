using TrayX.Windows;

namespace TrayX.IntegrationTests;

public sealed class WindowsSupportTests
{
    [Fact]
    public void CurrentMachine_MeetsMinimumWindowsBuild()
    {
        Assert.True(
            WindowsSupport.IsSupported,
            $"TrayX requires Windows 11 build {WindowsSupport.MinimumBuild} or newer.");
    }
}
