using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;
using TrayX.Contracts;

namespace TrayX.Windows.Shell;

public sealed class TrayIconSettingsService
{
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";

    private static readonly ConcurrentDictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<TrayIconDto> GetIcons()
    {
        var icons = new List<TrayIconDto>();

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: false);
            if (root is null)
            {
                return icons;
            }

            foreach (var keyName in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(keyName, writable: false);
                if (key is null)
                {
                    continue;
                }

                var executable = KnownFolderPaths.Resolve(key.GetValue("ExecutablePath") as string);
                if (executable.Length == 0)
                {
                    continue;
                }

                var promoted = key.GetValue("IsPromoted") is int value && value != 0;
                var tooltip = key.GetValue("InitialTooltip") as string;
                icons.Add(new TrayIconDto(
                    keyName,
                    ResolveName(executable, tooltip),
                    executable,
                    promoted,
                    tooltip,
                    "windows"));
            }
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
            UnauthorizedAccessException or
            IOException)
        {
        }

        return icons
            .OrderByDescending(icon => icon.IsPromoted)
            .ThenBy(icon => icon.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public TrayIconDto SetPromoted(string keyName, bool promoted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        if (keyName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Некорректный ключ значка.", nameof(keyName));
        }

        using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: true)
            ?? throw new InvalidOperationException("Windows ещё не сохраняла настройки значков трея.");
        using var key = root.OpenSubKey(keyName, writable: true)
            ?? throw new InvalidOperationException("Значок не найден. Обновите список.");

        key.SetValue("IsPromoted", promoted ? 1 : 0, RegistryValueKind.DWord);

        var executable = KnownFolderPaths.Resolve(key.GetValue("ExecutablePath") as string);
        var tooltip = key.GetValue("InitialTooltip") as string;

        return new TrayIconDto(
            keyName,
            ResolveName(executable, tooltip),
            executable,
            promoted,
            tooltip,
            "windows");
    }

    public string? GetIconImage(string keyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyName);

        if (keyName.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException("Некорректный ключ значка.", nameof(keyName));
        }

        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey, writable: false);
            using var key = root?.OpenSubKey(keyName, writable: false);
            if (key?.GetValue("IconSnapshot") is not byte[] snapshot ||
                snapshot.Length == 0 ||
                snapshot.Length > 512 * 1024)
            {
                return null;
            }

            return Convert.ToBase64String(snapshot);
        }
        catch (Exception exception) when (
            exception is System.Security.SecurityException or
            UnauthorizedAccessException or
            IOException)
        {
            return null;
        }
    }

    private static string ResolveName(string executable, string? tooltip)
    {
        if (Names.TryGetValue(executable, out var cached))
        {
            return cached;
        }

        var resolved = ResolveNameCore(executable, tooltip);
        Names[executable] = resolved;
        return resolved;
    }

    private static string ResolveNameCore(string executable, string? tooltip)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);
            var description = info.FileDescription?.Trim();
            if (!string.IsNullOrWhiteSpace(description))
            {
                return description;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }

        var file = Path.GetFileNameWithoutExtension(executable);
        if (!string.IsNullOrWhiteSpace(file))
        {
            return file;
        }

        return string.IsNullOrWhiteSpace(tooltip) ? "Значок Windows" : tooltip;
    }
}

public sealed class ExplorerService
{
    public void Restart()
    {
        var processes = Process.GetProcessesByName("explorer");
        var failures = new List<Exception>();
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: false);
                        if (!process.WaitForExit(5000))
                        {
                            failures.Add(new TimeoutException(
                                $"Проводник с PID {process.Id} не завершился за 5 секунд."));
                        }
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                    if (!HasExited(process))
                    {
                        failures.Add(exception);
                    }
                }
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Не удалось корректно перезапустить Проводник.", failures);
        }

        if (processes.Length == 0 || !WaitForExplorer(TimeSpan.FromSeconds(3)))
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            if (!WaitForExplorer(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Проводник не запустился после перезапуска.");
            }
        }
    }

    private static bool WaitForExplorer(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var processes = Process.GetProcessesByName("explorer");
            try
            {
                if (processes.Length > 0)
                {
                    return true;
                }
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
