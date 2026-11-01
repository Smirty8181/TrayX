using Microsoft.Data.Sqlite;
using TrayX.Application.Configuration;
using TrayX.Application.Profiles;
using TrayX.Domain.Entities;
using TrayX.Storage.Sqlite;

namespace TrayX.Storage.Profiles;

public sealed class ProfileStore : IProfileStore
{
    private readonly TrayXPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public ProfileStore(TrayXPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<IReadOnlyList<Profile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, name, volume, muted, windows_theme, accent, created_at " +
                "FROM profiles ORDER BY created_at DESC;";

            var profiles = new List<Profile>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                profiles.Add(new Profile
                {
                    Id = Guid.Parse(reader.GetString(0)),
                    Name = reader.GetString(1),
                    VolumePercent = reader.GetInt32(2),
                    IsMuted = reader.GetInt32(3) != 0,
                    WindowsTheme = reader.GetString(4),
                    Accent = reader.GetString(5),
                    CreatedAt = DateTimeOffset.Parse(
                        reader.GetString(6),
                        System.Globalization.CultureInfo.InvariantCulture)
                });
            }

            return profiles;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Profile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, name, volume, muted, windows_theme, accent, created_at " +
                "FROM profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new Profile
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                VolumePercent = reader.GetInt32(2),
                IsMuted = reader.GetInt32(3) != 0,
                WindowsTheme = reader.GetString(4),
                Accent = reader.GetString(5),
                CreatedAt = DateTimeOffset.Parse(
                    reader.GetString(6),
                    System.Globalization.CultureInfo.InvariantCulture)
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO profiles (id, name, volume, muted, windows_theme, accent, created_at) " +
                "VALUES ($id, $name, $volume, $muted, $theme, $accent, $created) " +
                "ON CONFLICT(id) DO UPDATE SET " +
                "name = excluded.name, volume = excluded.volume, muted = excluded.muted, " +
                "windows_theme = excluded.windows_theme, accent = excluded.accent;";

            command.Parameters.AddWithValue("$id", profile.Id.ToString());
            command.Parameters.AddWithValue("$name", profile.Name);
            command.Parameters.AddWithValue("$volume", profile.VolumePercent);
            command.Parameters.AddWithValue("$muted", profile.IsMuted ? 1 : 0);
            command.Parameters.AddWithValue("$theme", profile.WindowsTheme);
            command.Parameters.AddWithValue("$accent", profile.Accent);
            command.Parameters.AddWithValue(
                "$created",
                profile.CreatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM profiles WHERE id = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
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
                    "CREATE TABLE IF NOT EXISTS profiles (" +
                    "id TEXT PRIMARY KEY, " +
                    "name TEXT NOT NULL, " +
                    "volume INTEGER NOT NULL, " +
                    "muted INTEGER NOT NULL, " +
                    "windows_theme TEXT NOT NULL, " +
                    "accent TEXT NOT NULL, " +
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
