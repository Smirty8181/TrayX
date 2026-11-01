using System.Reflection;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Contracts;

namespace TrayX.Core.Hosting;

public sealed class CoreRuntimeInfo
{
    public CoreRuntimeInfo()
    {
        var version = typeof(CoreRuntimeInfo).Assembly.GetName().Version;
        Version = version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        StartedAt = DateTimeOffset.Now;
    }

    public string Version { get; }

    public DateTimeOffset StartedAt { get; }

    public int WindowsBuild => Environment.OSVersion.Version.Build;

    public string UserName => Environment.UserName;
}

public sealed class CoreControl : ICoreControl, IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private int _requested;

    public CancellationToken Token => _lifetime.Token;

    public void RequestShutdown()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 1)
        {
            return;
        }

        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose() => _lifetime.Dispose();
}

public sealed class ModuleRunner
{
    private readonly IReadOnlyList<ITrayXModule> _modules;
    private readonly ILogger<ModuleRunner> _logger;
    private readonly HashSet<string> _started = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _errors = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public ModuleRunner(IEnumerable<ITrayXModule> modules, ILogger<ModuleRunner> logger)
    {
        _modules = modules.ToList();
        _logger = logger;
    }

    public IReadOnlyList<ITrayXModule> Modules => _modules;

    public async Task StartAllAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var module in _modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_started.Contains(module.Id))
                    {
                        continue;
                    }
                }

                try
                {
                    await module.StartAsync(cancellationToken).ConfigureAwait(false);
                    lock (_sync)
                    {
                        _started.Add(module.Id);
                        _errors.Remove(module.Id);
                    }

                    _logger.LogInformation("Module {Id} started", module.Id);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lock (_sync)
                    {
                        _errors[module.Id] = exception.Message;
                    }

                    _logger.LogError(exception, "Module {Id} failed to start", module.Id);
                }
            }
        }
        catch (OperationCanceledException)
        {
            using var rollbackTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await StopAllAsync(rollbackTimeout.Token).ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        for (var index = _modules.Count - 1; index >= 0; index--)
        {
            var module = _modules[index];
            lock (_sync)
            {
                if (!_started.Contains(module.Id))
                {
                    continue;
                }
            }

            try
            {
                await module.StopAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _started.Remove(module.Id);
                    _errors.Remove(module.Id);
                }

                _logger.LogInformation("Module {Id} stopped", module.Id);
            }
            catch (OperationCanceledException exception)
            {
                lock (_sync)
                {
                    _errors[module.Id] = string.IsNullOrWhiteSpace(exception.Message)
                        ? "Остановка модуля отменена."
                        : exception.Message;
                }

                _logger.LogWarning(exception, "Module {Id} stop was cancelled", module.Id);
            }
            catch (Exception exception)
            {
                lock (_sync)
                {
                    _errors[module.Id] = exception.Message;
                }

                _logger.LogWarning(exception, "Module {Id} failed to stop", module.Id);
            }
        }
    }

    public IReadOnlyList<ModuleStatusDto> GetStatuses()
    {
        lock (_sync)
        {
            return _modules
                .Select(module => new ModuleStatusDto(
                    module.Id,
                    module.DisplayName,
                    module.IsAvailable,
                    _started.Contains(module.Id),
                    _errors.TryGetValue(module.Id, out var error) ? error : null))
                .ToList();
        }
    }

    public bool IsRunning(string id)
    {
        lock (_sync)
        {
            return _started.Contains(id);
        }
    }
}

public static class CoreExecutablePath
{
    public static string Resolve()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "TrayX.Core.exe");
        if (File.Exists(appHost))
        {
            return appHost;
        }

        var process = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(process) ? appHost : process;
    }
}

public static class AssemblyVersionInfo
{
    public static string Read(Assembly assembly)
    {
        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
