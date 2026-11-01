using TrayX.Windows.Shell;

namespace TrayX.IntegrationTests;

public sealed class TrayAppWindowEventMonitorIntegrationTests
{
    [Fact]
    public void StartAndDispose_RegistersAndReleasesWindowEventHooks()
    {
        using var monitor = new TrayAppWindowEventMonitor(
            () => { },
            exception => throw new InvalidOperationException("The event monitor failed.", exception));

        monitor.Start();
    }
}
