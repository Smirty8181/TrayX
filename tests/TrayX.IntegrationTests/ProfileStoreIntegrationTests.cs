using TrayX.Application.Configuration;
using TrayX.Domain.Entities;
using TrayX.Storage.Profiles;

namespace TrayX.IntegrationTests;

public sealed class ProfileStoreIntegrationTests
{
    [Fact]
    public async Task ProfileStore_CreatesUpdatesListsAndDeletesProfiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-profiles-{Guid.NewGuid():N}");
        try
        {
            var store = new ProfileStore(new TrayXPaths(root));
            var profile = Profile.Create(" Work ", 42, true, "light", "#3B82F6");

            await store.SaveAsync(profile);

            var saved = await store.GetAsync(profile.Id);
            Assert.NotNull(saved);
            Assert.Equal("Work", saved.Name);
            Assert.Equal(42, saved.VolumePercent);
            Assert.True(saved.IsMuted);
            Assert.Equal("light", saved.WindowsTheme);
            Assert.Equal("#3B82F6", saved.Accent);
            Assert.Equal(profile.CreatedAt, saved.CreatedAt);

            var updated = saved with { Name = "Updated", VolumePercent = 85, IsMuted = false };
            await store.SaveAsync(updated);

            var listed = Assert.Single(await store.GetAllAsync());
            Assert.Equal("Updated", listed.Name);
            Assert.Equal(85, listed.VolumePercent);
            Assert.False(listed.IsMuted);
            Assert.Equal(profile.CreatedAt, listed.CreatedAt);

            await store.DeleteAsync(profile.Id);

            Assert.Null(await store.GetAsync(profile.Id));
            Assert.Empty(await store.GetAllAsync());
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
    public async Task SaveAsync_WaitsForAnotherWriterAfterTheStoreHasOpenedItsConnection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-profiles-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            var store = new ProfileStore(paths);
            await store.SaveAsync(Profile.Create("Seed", 50, false, "dark", "#7C5CFF"));

            await SqliteStoreTestHelpers.HoldWriteLockAndStartAsync(
                paths,
                () => store.SaveAsync(Profile.Create("Second", 75, true, "light", "#3B82F6")));

            Assert.Equal(2, (await store.GetAllAsync()).Count);
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
    public async Task GetAllAsync_RejectsCorruptDatabaseAndPreservesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-profiles-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            Directory.CreateDirectory(root);
            var corruptData = new byte[] { 0x54, 0x72, 0x61, 0x79, 0x58, 0x00, 0xFF };
            await File.WriteAllBytesAsync(paths.DatabaseFile, corruptData);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new ProfileStore(paths).GetAllAsync());

            Assert.Contains(paths.DatabaseFile, exception.Message, StringComparison.Ordinal);
            Assert.Equal(corruptData, await File.ReadAllBytesAsync(paths.DatabaseFile));
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
