using TrayX.Modules.SoundEvents;

namespace TrayX.IntegrationTests;

public sealed class SoundEventsPathIntegrationTests
{
    [Theory]
    [InlineData(@"\\server\share\notification.wav")]
    [InlineData(@"\\?\UNC\server\share\notification.wav")]
    [InlineData(@"\\.\C:\notification.wav")]
    public void IsRemotePath_RejectsNetworkAndDevicePaths(string path)
    {
        Assert.True(SoundEventsModule.IsRemotePath(path));
    }

    [Fact]
    public void IsRemotePath_AllowsLocalDrivePaths()
    {
        var localRoot = Path.GetPathRoot(Environment.SystemDirectory);
        Assert.NotNull(localRoot);

        Assert.False(SoundEventsModule.IsRemotePath(Path.Combine(localRoot, "sounds", "notification.wav")));
    }
}
