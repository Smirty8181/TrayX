using TrayX.Application.Configuration;
using TrayX.Storage.Settings;

namespace TrayX.IntegrationTests;

public sealed class SettingsStoreIntegrationTests
{
    [Fact]
    public async Task BrokenConfiguration_IsPreservedBeforeSettingsAreRewritten()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            Directory.CreateDirectory(root);
            const string invalidJson = "{ invalid settings";
            await File.WriteAllTextAsync(paths.ConfigurationFile, invalidJson);

            var store = new SettingsStore(paths);

            Assert.NotNull(store.LastError);
            var backup = Assert.Single(Directory.GetFiles(root, "config.json.invalid-*"));
            Assert.Equal(invalidJson, await File.ReadAllTextAsync(backup));

            await store.UpdateAsync(current => current with
            {
                Accent = "#22D3EE",
                ClockFormat = "24"
            });

            var reloaded = new SettingsStore(paths);
            Assert.Equal("#22D3EE", reloaded.Current.Accent);
            Assert.Equal("24", reloaded.Current.ClockFormat);
            Assert.Null(reloaded.LastError);
            Assert.Equal(invalidJson, await File.ReadAllTextAsync(backup));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ConcurrentUpdates_MergeIndependentChangesAgainstTheLatestSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-settings-{Guid.NewGuid():N}");
        try
        {
            var store = new SettingsStore(new TrayXPaths(root));

            await Task.WhenAll(
                store.UpdateAsync(current => current with { Accent = "#22D3EE" }),
                store.UpdateAsync(current => current with { ClockFormat = "24" }));

            Assert.Equal("#22D3EE", store.Current.Accent);
            Assert.Equal("24", store.Current.ClockFormat);
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
