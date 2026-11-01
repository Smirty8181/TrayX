using TrayX.Application.Modules;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Apps;

namespace TrayX.Modules.AppLauncher;

public sealed class AppLauncherModule : ITrayXModule
{
    private readonly AppCatalogService _catalog;

    public AppLauncherModule(AppCatalogService catalog)
    {
        _catalog = catalog;
    }

    public string Id => "apps";

    public string DisplayName => "Приложения";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<AppEntryDto>> GetCatalogAsync(CancellationToken cancellationToken) =>
        _catalog.GetCatalogAsync(cancellationToken);

    public Task LaunchAsync(LaunchAppRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _catalog.LaunchAsync(request.Id, request.ShortcutPath, request.Source, cancellationToken);
    }
}
