using TrayX.Application.Configuration;

namespace TrayX.IntegrationTests;

public sealed class TrayXPathsIntegrationTests
{
    [Fact]
    public void EnsureCreated_CreatesDirectoryTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        var paths = new TrayXPaths(root);

        try
        {
            paths.EnsureCreated();

            Assert.True(Directory.Exists(paths.RootDirectory));
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Assert.True(Directory.Exists(paths.CacheDirectory));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
