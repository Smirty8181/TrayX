using Microsoft.Data.Sqlite;

namespace TrayX.Storage.Sqlite;

internal static class SqliteDatabaseHealth
{
    public static async Task ValidateAsync(
        SqliteConnection connection,
        string databaseFile,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        try
        {
            var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SQLite database integrity check failed for '{databaseFile}': {result ?? "no result"}.");
            }
        }
        catch (SqliteException exception) when (IsCorruption(exception))
        {
            throw new InvalidDataException(
                $"SQLite database is corrupt and was preserved at '{databaseFile}'.",
                exception);
        }
    }

    public static InvalidDataException DescribeCorruption(SqliteException exception, string databaseFile) =>
        new($"SQLite database is corrupt and was preserved at '{databaseFile}'.", exception);

    public static bool IsCorruption(SqliteException exception) =>
        exception.SqliteErrorCode is 11 or 26;
}
