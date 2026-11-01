using System.Text;
using System.Text.Json;
using TrayX.Application.Configuration;
using TrayX.Application.Settings;
using TrayX.Contracts;

namespace TrayX.Storage.Settings;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly TrayXPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrayXSettings _current = TrayXSettings.Default;

    public SettingsStore(TrayXPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _current = Load();
    }

    public TrayXSettings Current => Volatile.Read(ref _current);

    public string? LastError { get; private set; }

    public event EventHandler<TrayXSettings>? Changed;

    public async Task UpdateAsync(TrayXSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await UpdateAsync(_ => settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(
        Func<TrayXSettings, TrayXSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TrayXSettings normalized;
        try
        {
            normalized = (update(Current) ??
                throw new InvalidOperationException("The settings update returned null.")).Normalize();
            await SaveAsync(normalized, cancellationToken).ConfigureAwait(false);
            LastError = null;
            Volatile.Write(ref _current, normalized);
        }
        finally
        {
            _gate.Release();
        }

        if (ReferenceEquals(Current, normalized))
        {
            Changed?.Invoke(this, normalized);
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TrayXSettings loaded;
        try
        {
            loaded = Load();
            Volatile.Write(ref _current, loaded);
        }
        finally
        {
            _gate.Release();
        }

        if (ReferenceEquals(Current, loaded))
        {
            Changed?.Invoke(this, loaded);
        }
    }

    private TrayXSettings Load()
    {
        var file = _paths.ConfigurationFile;
        if (!File.Exists(file))
        {
            return TrayXSettings.Default;
        }

        try
        {
            var json = File.ReadAllText(file, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<TrayXSettings>(json, SerializerOptions);
            return (settings ?? TrayXSettings.Default).Normalize();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LastError = $"Не удалось прочитать настройки: {exception.Message}";
            TryPreserveBrokenFile(file);
            return TrayXSettings.Default;
        }
    }

    private static void TryPreserveBrokenFile(string file)
    {
        try
        {
            var backup = $"{file}.invalid-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(file, backup, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task SaveAsync(TrayXSettings settings, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootDirectory);

        var json = JsonSerializer.Serialize(settings, SerializerOptions);
        var target = _paths.ConfigurationFile;
        var temporary = target + ".tmp";
        var backup = target + ".bak";

        await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (File.Exists(target))
            {
                File.Replace(temporary, target, backup, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporary, target);
            }
        }
        catch (IOException)
        {
            File.Copy(temporary, target, overwrite: true);
            File.Delete(temporary);
        }
    }
}
