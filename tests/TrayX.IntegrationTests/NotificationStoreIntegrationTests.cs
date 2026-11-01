using Microsoft.Data.Sqlite;
using TrayX.Application.Configuration;
using TrayX.Domain.Entities;
using TrayX.Storage.Notifications;

namespace TrayX.IntegrationTests;

public sealed class NotificationStoreIntegrationTests
{
    [Fact]
    public async Task Notifications_PersistInOrderAndCanBeCleared()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-notifications-{Guid.NewGuid():N}");
        try
        {
            var store = new NotificationStore(new TrayXPaths(root));
            var first = new NotificationRecord(
                Guid.NewGuid(),
                "Device",
                "Audio output connected",
                DateTimeOffset.UtcNow.AddMinutes(-1));
            var second = new NotificationRecord(
                Guid.NewGuid(),
                "Profile",
                "Work profile applied",
                DateTimeOffset.UtcNow);

            await store.AddAsync(first);
            await store.AddAsync(second);

            var restored = await new NotificationStore(new TrayXPaths(root)).GetRecentAsync(10);

            Assert.Equal([second.Id, first.Id], restored.Select(item => item.Id));
            Assert.Equal(second.Title, restored[0].Title);
            Assert.Equal(second.Message, restored[0].Message);

            await store.ClearAsync();

            Assert.Empty(await store.GetRecentAsync(10));
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
    public async Task GetRecentAsync_ClampsTheRequestedLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-notifications-{Guid.NewGuid():N}");
        try
        {
            var store = new NotificationStore(new TrayXPaths(root));
            await store.AddAsync(new NotificationRecord(
                Guid.NewGuid(),
                "Title",
                "Message",
                DateTimeOffset.UtcNow));

            Assert.Single(await store.GetRecentAsync(0));
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
    public async Task AddAsync_PrunesRecordsBeyondTheRetentionLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-notifications-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            var store = new NotificationStore(paths);
            var oldest = new NotificationRecord(
                Guid.NewGuid(),
                "Oldest",
                "Must be pruned",
                DateTimeOffset.UtcNow.AddDays(-2));
            await store.AddAsync(oldest);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabaseFile,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText =
                    "INSERT INTO notifications (id, title, message, created_at) " +
                    "VALUES ($id, $title, $message, $created);";
                var idParameter = command.Parameters.Add("$id", SqliteType.Text);
                var titleParameter = command.Parameters.Add("$title", SqliteType.Text);
                var messageParameter = command.Parameters.Add("$message", SqliteType.Text);
                var createdParameter = command.Parameters.Add("$created", SqliteType.Text);

                for (var index = 0; index < 1000; index++)
                {
                    idParameter.Value = Guid.NewGuid().ToString();
                    titleParameter.Value = "Retained";
                    messageParameter.Value = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    createdParameter.Value = DateTimeOffset.UtcNow
                        .AddMinutes(index)
                        .ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                    await command.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
            }

            var newest = new NotificationRecord(
                Guid.NewGuid(),
                "Newest",
                "Must be retained",
                DateTimeOffset.UtcNow.AddDays(1));
            await store.AddAsync(newest);

            var retained = await store.GetRecentAsync(2000);
            Assert.Equal(1000, retained.Count);
            Assert.Equal(newest.Id, retained[0].Id);
            Assert.DoesNotContain(retained, item => item.Id == oldest.Id);
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
    public async Task AddAsync_WaitsForAnotherWriterAfterTheStoreHasOpenedItsConnection()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-notifications-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            var store = new NotificationStore(paths);
            await store.AddAsync(new NotificationRecord(Guid.NewGuid(), "Seed", "Initialized", DateTimeOffset.UtcNow));

            await SqliteStoreTestHelpers.HoldWriteLockAndStartAsync(
                paths,
                () => store.AddAsync(new NotificationRecord(
                    Guid.NewGuid(),
                    "Concurrent",
                    "Wait for the current writer",
                    DateTimeOffset.UtcNow)));
            Assert.Equal(2, (await store.GetRecentAsync(10)).Count);
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
    public async Task GetRecentAsync_RejectsCorruptDatabaseAndPreservesIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"TrayX-notifications-{Guid.NewGuid():N}");
        try
        {
            var paths = new TrayXPaths(root);
            Directory.CreateDirectory(root);
            var corruptData = new byte[] { 0x54, 0x72, 0x61, 0x79, 0x58, 0x00, 0xFF };
            await File.WriteAllBytesAsync(paths.DatabaseFile, corruptData);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => new NotificationStore(paths).GetRecentAsync(10));

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
