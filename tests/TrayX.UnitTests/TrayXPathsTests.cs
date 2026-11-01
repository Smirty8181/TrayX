using TrayX.Application.Configuration;

namespace TrayX.UnitTests;

public sealed class TrayXPathsTests
{
    [Fact]
    public void ForCurrentUser_PointsToRoamingAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var paths = TrayXPaths.ForCurrentUser();

        Assert.Equal(Path.Combine(appData, TrayXPaths.ProductFolderName), paths.RootDirectory);
    }

    [Fact]
    public void DerivedPaths_StayInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));

        var paths = new TrayXPaths(root);

        Assert.Equal(Path.Combine(root, "logs"), paths.LogsDirectory);
        Assert.Equal(Path.Combine(root, "cache"), paths.CacheDirectory);
        Assert.Equal(Path.Combine(root, "trayx.db"), paths.DatabaseFile);
        Assert.Equal(Path.Combine(root, "config.json"), paths.ConfigurationFile);
        Assert.Equal(Path.Combine(root, "tray-app-recovery.json"), paths.TrayAppRecoveryFile);
    }

    [Fact]
    public void Constructor_RejectsBlankRoot()
    {
        Assert.Throws<ArgumentException>(() => new TrayXPaths(" "));
    }
}
