using TrayX.UI.Services;

namespace TrayX.IntegrationTests;

public sealed class UiExceptionLogIntegrationTests
{
    [Fact]
    public void Write_RotatesLogsAndKeepsOnlyTheConfiguredNumberOfFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-ui-log-{Guid.NewGuid():N}");
        try
        {
            UiExceptionLog.Initialize(root, maxFileSizeBytes: 512, retainedFileCount: 3);

            for (var index = 0; index < 8; index++)
            {
                UiExceptionLog.Write("integration", new string((char)('a' + index), 256));
            }

            var files = Directory.GetFiles(root, "ui-*.log*");
            Assert.Equal(3, files.Length);
            Assert.Contains(files, file => file.EndsWith(".log", StringComparison.Ordinal));
            Assert.All(files, file => Assert.True(new FileInfo(file).Length <= 1024));
        }
        finally
        {
            UiExceptionLog.ResetForTests();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
