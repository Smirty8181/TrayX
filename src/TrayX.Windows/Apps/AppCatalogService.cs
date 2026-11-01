using System.Diagnostics;
using TrayX.Contracts;

namespace TrayX.Windows.Apps;

public sealed class AppCatalogService
{
    private readonly object _sync = new();
    private Task<IReadOnlyList<AppEntryDto>>? _catalog;
    private DateTimeOffset _createdAt;

    public Task<IReadOnlyList<AppEntryDto>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_catalog is not null &&
                DateTimeOffset.UtcNow - _createdAt < TimeSpan.FromMinutes(10))
            {
                return _catalog;
            }

            _createdAt = DateTimeOffset.UtcNow;
            _catalog = Task.Run(BuildCatalog, cancellationToken);
            return _catalog;
        }
    }

    public async Task LaunchAsync(string id, string shortcutPath, string source, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(shortcutPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var catalog = await GetCatalogAsync(cancellationToken).ConfigureAwait(false);
        var entry = catalog.FirstOrDefault(app =>
            string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(app.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(app.Source, source, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException("The requested application is not in the application catalog.");
        }

        ProcessStartInfo startInfo;
        if (string.Equals(entry.Source, "apps", StringComparison.OrdinalIgnoreCase))
        {
            var appId = entry.ShortcutPath["apps:".Length..];

            startInfo = new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}")
            {
                UseShellExecute = true
            };
        }
        else
        {
            if (!File.Exists(entry.ShortcutPath))
            {
                throw new FileNotFoundException("Ярлык приложения не найден.", entry.ShortcutPath);
            }

            startInfo = new ProcessStartInfo(entry.ShortcutPath) { UseShellExecute = true };
        }

        using var process = Process.Start(startInfo);
    }

    private static IReadOnlyList<AppEntryDto> BuildCatalog()
    {
        var apps = new Dictionary<string, AppEntryDto>(StringComparer.CurrentCultureIgnoreCase);

        AddStartMenuShortcuts(apps);
        AddPackagedApps(apps);

        return apps.Values
            .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void AddStartMenuShortcuts(IDictionary<string, AppEntryDto> apps)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
        };

        foreach (var folder in folders)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in EnumerateShortcutFiles(folder))
            {
                var name = Path.GetFileNameWithoutExtension(file).Trim();
                if (name.Length == 0 || IsHelpOrUninstaller(name))
                {
                    continue;
                }

                var key = name;
                if (apps.ContainsKey(key))
                {
                    continue;
                }

                apps[key] = new AppEntryDto(
                    "start:" + file,
                    name,
                    file,
                    null,
                    "start");
            }
        }
    }

    private static List<string> EnumerateShortcutFiles(string root)
    {
        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            try
            {
                foreach (var file in Directory.GetFiles(current))
                {
                    if (file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(directory);
            }
        }

        return results;
    }

    private static void AddPackagedApps(IDictionary<string, AppEntryDto> apps)
    {
        object? shell = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return;
            }

            dynamic dynamicShell = shell;
            dynamic folder = dynamicShell.NameSpace("shell:AppsFolder");
            if (folder is null)
            {
                return;
            }

            foreach (dynamic item in folder.Items())
            {
                string name;
                string appId;
                try
                {
                    name = (string)item.Name;
                    appId = (string)item.Path;
                }
                catch (Exception)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(appId) ||
                    apps.ContainsKey(name))
                {
                    continue;
                }

                apps[name] = new AppEntryDto(
                    "apps:" + appId,
                    name,
                    "apps:" + appId,
                    null,
                    "apps");
            }
        }
        catch (Exception)
        {
            // На системах без Shell.Application или AppsFolder каталог ярлыков остаётся рабочим.
        }
        finally
        {
            if (shell is not null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
            {
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
        }
    }

    private static bool IsHelpOrUninstaller(string name) =>
        name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Удалить", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Readme", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Release Notes", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Liesmich", StringComparison.OrdinalIgnoreCase);
}
