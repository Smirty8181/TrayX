using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;

namespace TrayX.Core.Hosting;

public sealed class UiLauncher : IUiLauncher
{
    private readonly ILogger<UiLauncher> _logger;
    private readonly object _sync = new();
    private DateTimeOffset _lastLaunch = DateTimeOffset.MinValue;

    public UiLauncher(ILogger<UiLauncher> logger)
    {
        _logger = logger;
    }

    public void Open(string page)
    {
        var safePage = SanitizePage(page);

        lock (_sync)
        {
            if (DateTimeOffset.UtcNow - _lastLaunch < TimeSpan.FromSeconds(2))
            {
                return;
            }

            _lastLaunch = DateTimeOffset.UtcNow;
        }

        var executable = LocateUiExecutable();
        if (executable is null)
        {
            _logger.LogError("TrayX.UI.exe was not found next to Core or in the development output");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            };
            startInfo.ArgumentList.Add("--page");
            startInfo.ArgumentList.Add(safePage);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogError("Windows did not return a process handle for TrayX.UI");
                return;
            }

            _logger.LogInformation("TrayX.UI started with page {Page}", safePage);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to start TrayX.UI");
        }
    }

    private static string? LocateUiExecutable()
    {
        var besideCore = Path.Combine(AppContext.BaseDirectory, "TrayX.UI.exe");
        if (File.Exists(besideCore))
        {
            return besideCore;
        }

        var root = FindRepositoryRoot();
        if (root is null)
        {
            return null;
        }

        var binDirectory = Path.Combine(root, "src", "TrayX.UI", "bin");
        if (!Directory.Exists(binDirectory))
        {
            return null;
        }

        try
        {
            return Directory
                .EnumerateFiles(binDirectory, "TrayX.UI.exe", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrayX.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string SanitizePage(string page)
    {
        if (string.IsNullOrWhiteSpace(page))
        {
            return "dashboard";
        }

        var trimmed = page.Trim().ToLowerInvariant();
        var allowed = trimmed.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray();
        var result = new string(allowed);
        return result.Length == 0 ? "dashboard" : result[..Math.Min(result.Length, 32)];
    }
}
