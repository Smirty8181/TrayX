using Microsoft.Extensions.Logging;
using TrayX.Application.Modules;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Display;

namespace TrayX.Modules.MultiMonitor;

public sealed class MultiMonitorModule : ITrayXModule
{
    private readonly ILogger<MultiMonitorModule> _logger;

    public MultiMonitorModule(DisplayService service, ILogger<MultiMonitorModule> logger)
    {
        Service = service;
        _logger = logger;
    }

    public DisplayService Service { get; }

    public string Id => "displays";

    public string DisplayName => "Мониторы";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var displays = Service.GetDisplays();
        _logger.LogInformation(
            "Displays detected: {Count} ({Names})",
            displays.Count,
            string.Join(", ", displays.Select(display => display.FriendlyName)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public IReadOnlyList<DisplayDto> GetDisplays() => Service.GetDisplays();

    public IReadOnlyList<DisplayDto> SetBrightness(int displayIndex, int percent, string? stableId = null)
    {
        Service.SetBrightness(displayIndex, percent, stableId);
        return Service.GetDisplays();
    }
}
