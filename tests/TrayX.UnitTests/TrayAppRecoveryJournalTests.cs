using System.Text;
using TrayX.Application.Configuration;
using TrayX.Contracts;
using TrayX.Storage.TrayApps;

namespace TrayX.UnitTests;

public sealed class TrayAppRecoveryJournalTests
{
    [Fact]
    public async Task PrepareAndComplete_PersistExactWindowIdentity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var first = CreateRecord(101, 202, 303);
            var second = CreateRecord(102, 202, 303);

            await journal.PrepareAsync(first);
            await journal.PrepareAsync(second);

            var reopened = new TrayAppRecoveryJournal(new TrayXPaths(root));
            Assert.Equal(new[] { first, second }, await reopened.GetPendingAsync());

            await reopened.CompleteAsync(first.WindowHandle, first.ProcessId, first.ProcessStartTimeUtcTicks + 1);
            Assert.Equal(2, (await reopened.GetPendingAsync()).Count);

            await reopened.CompleteAsync(first.WindowHandle, first.ProcessId, first.ProcessStartTimeUtcTicks);
            Assert.Equal(new[] { second }, await reopened.GetPendingAsync());
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsInvalidWindowIdentity()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var invalid = CreateRecord(0, 202, 303);

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.PrepareAsync(invalid));
            Assert.False(File.Exists(new TrayXPaths(root).TrayAppRecoveryFile));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task PrepareAsync_RejectsUnsupportedWindowState()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var invalid = CreateRecord(101, 202, 303) with { WindowShowCommand = 99 };

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.PrepareAsync(invalid));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GetPendingAsync_RejectsCorruptJournalAndPreservesItWhenNoBackupExists()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new TrayXPaths(root);
            const string corruptContent = "{ invalid";
            await File.WriteAllTextAsync(paths.TrayAppRecoveryFile, corruptContent, Encoding.UTF8);
            var journal = new TrayAppRecoveryJournal(paths);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => journal.GetPendingAsync());
            Assert.Contains(paths.TrayAppRecoveryFile, exception.Message, StringComparison.Ordinal);
            Assert.Equal(corruptContent, await File.ReadAllTextAsync(paths.TrayAppRecoveryFile, Encoding.UTF8));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GetPendingAsync_QuarantinesCorruptJournalAndRestoresValidBackup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new TrayXPaths(root);
            var journal = new TrayAppRecoveryJournal(paths);
            var backupRecord = CreateRecord(101, 202, 303);
            await journal.PrepareAsync(backupRecord);
            await journal.PrepareAsync(CreateRecord(102, 202, 303));
            const string corruptContent = "{ invalid";
            await File.WriteAllTextAsync(paths.TrayAppRecoveryFile, corruptContent, Encoding.UTF8);

            var recovered = await new TrayAppRecoveryJournal(paths).GetPendingAsync();

            Assert.Equal(new[] { backupRecord }, recovered);
            Assert.Equal(new[] { backupRecord }, await new TrayAppRecoveryJournal(paths).GetPendingAsync());
            Assert.Equal(corruptContent, await File.ReadAllTextAsync(
                Directory.GetFiles(root, "tray-app-recovery.json.corrupt-*").Single(),
                Encoding.UTF8));
            Assert.True(File.Exists($"{paths.TrayAppRecoveryFile}.bak"));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    [Fact]
    public async Task GetPendingAsync_PreservesBothFilesAndFailsWhenJournalAndBackupAreCorrupt()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var paths = new TrayXPaths(root);
            const string corruptContent = "{ invalid primary";
            const string corruptBackupContent = "{ invalid backup";
            await File.WriteAllTextAsync(paths.TrayAppRecoveryFile, corruptContent, Encoding.UTF8);
            await File.WriteAllTextAsync($"{paths.TrayAppRecoveryFile}.bak", corruptBackupContent, Encoding.UTF8);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new TrayAppRecoveryJournal(paths).GetPendingAsync());

            Assert.Contains(paths.TrayAppRecoveryFile, exception.Message, StringComparison.Ordinal);
            Assert.Contains($"{paths.TrayAppRecoveryFile}.bak", exception.Message, StringComparison.Ordinal);
            Assert.Equal(corruptContent, await File.ReadAllTextAsync(paths.TrayAppRecoveryFile, Encoding.UTF8));
            Assert.Equal(corruptBackupContent, await File.ReadAllTextAsync(
                $"{paths.TrayAppRecoveryFile}.bak",
                Encoding.UTF8));
        }
        finally
        {
            DeleteTemporaryDirectory(root);
        }
    }

    private static TrayAppRecoveryRecord CreateRecord(long handle, int processId, long startTicks) =>
        new(handle, processId, startTicks, "TrayX.TestWindow", "Test window", 1, DateTimeOffset.UtcNow);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
