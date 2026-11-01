using Microsoft.Extensions.Logging;
using TrayX.Application.Modules;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.SystemInfo;

namespace TrayX.Modules.SystemMonitor;

public sealed class SystemMonitorModule : ITrayXModule
{
    private readonly ILogger<SystemMonitorModule> _logger;

    public SystemMonitorModule(SystemMetricsService service, ILogger<SystemMonitorModule> logger)
    {
        Service = service;
        _logger = logger;
    }

    public SystemMetricsService Service { get; }

    public string Id => "system";

    public string DisplayName => "Мониторинг системы";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Service.GetStats();
        _logger.LogInformation("System metrics baseline captured");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public SystemStatsDto GetStats() => Service.GetStats();
}
