using Microsoft.Data.Sqlite;
using TrayX.Application.Configuration;
using TrayX.Application.Notifications;
using TrayX.Domain.Entities;
using TrayX.Storage.Sqlite;

namespace TrayX.Storage.Notifications;

public sealed class NotificationStore : INotificationStore
{
    private const int RetainedCount = 1000;

    private readonly TrayXPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public NotificationStore(TrayXPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<IReadOnlyList<NotificationRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var boundedLimit = Math.Clamp(limit, 1, RetainedCount);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, title, message, created_at FROM notifications " +
                "ORDER BY created_at DESC, rowid DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$limit", boundedLimit);

            var notifications = new List<NotificationRecord>(boundedLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                notifications.Add(new NotificationRecord(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    reader.GetString(2),
                    DateTimeOffset.Parse(
                        reader.GetString(3),
                        System.Globalization.CultureInfo.InvariantCulture)));
            }

            return notifications;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(
        NotificationRecord notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (notification.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(notification.Title) ||
            string.IsNullOrWhiteSpace(notification.Message))
        {
            throw new ArgumentException("Уведомление должно иметь ID, заголовок и текст.", nameof(notification));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                "INSERT INTO notifications (id, title, message, created_at) " +
                "VALUES ($id, $title, $message, $created); " +
                "DELETE FROM notifications WHERE rowid NOT IN (" +
                "SELECT rowid FROM notifications ORDER BY created_at DESC, rowid DESC LIMIT $retained);";
            command.Parameters.AddWithValue("$id", notification.Id.ToString());
            command.Parameters.AddWithValue("$title", notification.Title);
            command.Parameters.AddWithValue("$message", notification.Message);
            command.Parameters.AddWithValue(
                "$created",
                notification.CreatedAt.ToUniversalTime().ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$retained", RetainedCount);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM notifications;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 3
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 3000;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (!_initialized)
            {
                await SqliteDatabaseHealth.ValidateAsync(
                    connection,
                    _paths.DatabaseFile,
                    cancellationToken).ConfigureAwait(false);
                command.CommandText =
                    "PRAGMA journal_mode = WAL;" +
                    "CREATE TABLE IF NOT EXISTS notifications (" +
                    "id TEXT PRIMARY KEY, " +
                    "title TEXT NOT NULL, " +
                    "message TEXT NOT NULL, " +
                    "created_at TEXT NOT NULL);";
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            return connection;
        }
        catch (SqliteException exception) when (SqliteDatabaseHealth.IsCorruption(exception))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw SqliteDatabaseHealth.DescribeCorruption(exception, _paths.DatabaseFile);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
