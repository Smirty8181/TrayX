using Microsoft.Data.Sqlite;
using TrayX.Application.Configuration;

namespace TrayX.IntegrationTests;

internal static class SqliteStoreTestHelpers
{
    public static async Task HoldWriteLockAndStartAsync(TrayXPaths paths, Func<Task> startWrite)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using (var begin = connection.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        var write = Task.Run(startWrite);
        await Task.Delay(150);
        await using (var commit = connection.CreateCommand())
        {
            commit.CommandText = "COMMIT;";
            await commit.ExecuteNonQueryAsync();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
