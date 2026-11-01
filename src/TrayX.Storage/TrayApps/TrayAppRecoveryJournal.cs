using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TrayX.Application.Configuration;
using TrayX.Application.TrayApps;
using TrayX.Contracts;

namespace TrayX.Storage.TrayApps;

public sealed class TrayAppRecoveryJournal : ITrayAppRecoveryJournal
{
    private const int MaximumRecords = 1000;
    private const long MaximumFileBytes = 1_048_576;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly TrayXPaths _paths;
    private readonly ILogger<TrayAppRecoveryJournal> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public TrayAppRecoveryJournal(TrayXPaths paths)
        : this(paths, NullLogger<TrayAppRecoveryJournal>.Instance)
    {
    }

    public TrayAppRecoveryJournal(
        TrayXPaths paths,
        ILogger<TrayAppRecoveryJournal> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<TrayAppRecoveryRecord>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRecordsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PrepareAsync(
        TrayAppRecoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        ValidateRecord(record);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await ReadRecordsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var index = records.FindIndex(existing =>
                existing.WindowHandle == record.WindowHandle &&
                existing.ProcessId == record.ProcessId &&
                existing.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks);
            if (index >= 0)
            {
                records[index] = record;
            }
            else
            {
                if (records.Count >= MaximumRecords)
                {
                    throw new InvalidOperationException(
                        $"The tray app recovery journal is limited to {MaximumRecords} records.");
                }

                records.Add(record);
            }

            await WriteRecordsAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CompleteAsync(
        long windowHandle,
        int processId,
        long processStartTimeUtcTicks,
        CancellationToken cancellationToken = default)
    {
        if (windowHandle == 0 || processId <= 0 || processStartTimeUtcTicks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowHandle), "A complete window identity is required.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = (await ReadRecordsAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = records.RemoveAll(record =>
                record.WindowHandle == windowHandle &&
                record.ProcessId == processId &&
                record.ProcessStartTimeUtcTicks == processStartTimeUtcTicks);
            if (removed > 0)
            {
                await WriteRecordsAsync(records, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<TrayAppRecoveryRecord>> ReadRecordsAsync(
        CancellationToken cancellationToken)
    {
        var file = _paths.TrayAppRecoveryFile;
        if (!File.Exists(file))
        {
            var backup = $"{file}.bak";
            if (File.Exists(backup))
            {
                var records = await ReadAndValidateRecordsAsync(backup, cancellationToken).ConfigureAwait(false);
                await RestoreBackupAsync(file, backup, cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Restored {Count} tray app recovery records from backup {BackupFile}",
                    records.Count,
                    backup);
                return records;
            }

            return Array.Empty<TrayAppRecoveryRecord>();
        }

        try
        {
            return await ReadAndValidateRecordsAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var backup = $"{file}.bak";
            if (!File.Exists(backup))
            {
                _logger.LogError(
                    exception,
                    "Tray app recovery journal {JournalFile} is corrupt and has no backup",
                    file);
                throw new InvalidDataException(
                    $"The tray app recovery journal is corrupt and was preserved at '{file}'.",
                    exception);
            }

            IReadOnlyList<TrayAppRecoveryRecord> backupRecords;
            try
            {
                backupRecords = await ReadAndValidateRecordsAsync(backup, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception backupException) when (
                backupException is JsonException or InvalidDataException)
            {
                _logger.LogError(
                    backupException,
                    "Tray app recovery journal {JournalFile} and backup {BackupFile} are corrupt",
                    file,
                    backup);
                throw new InvalidDataException(
                    $"The tray app recovery journal and its backup are corrupt. Both files were preserved: '{file}' and '{backup}'.",
                    new AggregateException(exception, backupException));
            }

            var quarantinedFile = $"{file}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            File.Move(file, quarantinedFile);
            try
            {
                await RestoreBackupAsync(file, backup, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (!File.Exists(file) && File.Exists(quarantinedFile))
                {
                    File.Move(quarantinedFile, file);
                }

                throw;
            }

            _logger.LogWarning(
                exception,
                "Restored {Count} tray app recovery records from backup {BackupFile}; corrupt journal preserved at {QuarantinedFile}",
                backupRecords.Count,
                backup,
                quarantinedFile);
            return backupRecords;
        }
    }

    private static async Task<IReadOnlyList<TrayAppRecoveryRecord>> ReadAndValidateRecordsAsync(
        string file,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The tray app recovery journal exceeds the 1 MB limit.");
        }

        var records = await JsonSerializer.DeserializeAsync<List<TrayAppRecoveryRecord>>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);
        if (records is null || records.Count > MaximumRecords)
        {
            throw new InvalidDataException("The tray app recovery journal has an invalid record count.");
        }

        foreach (var record in records)
        {
            ValidateRecord(record);
        }

        if (records.Select(GetIdentity).Distinct().Count() != records.Count)
        {
            throw new InvalidDataException("The tray app recovery journal contains duplicate window identities.");
        }

        return records;
    }

    private static async Task RestoreBackupAsync(
        string target,
        string backup,
        CancellationToken cancellationToken)
    {
        var temporary = $"{target}.{Guid.NewGuid():N}.restore";
        try
        {
            await using (var source = new FileStream(
                backup,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            File.Move(temporary, target);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task WriteRecordsAsync(
        IReadOnlyList<TrayAppRecoveryRecord> records,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootDirectory);

        var target = _paths.TrayAppRecoveryFile;
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    records,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(target))
            {
                File.Replace(temporary, target, $"{target}.bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, target);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static (long Handle, int ProcessId, long StartTicks) GetIdentity(
        TrayAppRecoveryRecord record) =>
        (record.WindowHandle, record.ProcessId, record.ProcessStartTimeUtcTicks);

    private static void ValidateRecord(TrayAppRecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.WindowHandle == 0 ||
            record.ProcessId <= 0 ||
            record.ProcessStartTimeUtcTicks <= 0 ||
            string.IsNullOrWhiteSpace(record.WindowClass) ||
            record.WindowClass.Length > 256 ||
            record.WindowTitle is null ||
            record.WindowTitle.Length > 2048 ||
            record.WindowShowCommand is not (1 or 2 or 3 or 7) ||
            record.PreparedAtUtc == default)
        {
            throw new InvalidDataException("The tray app recovery record is invalid.");
        }
    }
}
