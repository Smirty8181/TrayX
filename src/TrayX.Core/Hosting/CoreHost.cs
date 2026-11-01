using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Core.Ipc;
using TrayX.Windows.Startup;

namespace TrayX.Core.Hosting;

public sealed class CoreHost
{
    private readonly ISettingsStore _settings;
    private readonly StartupRegistrar _startup;
    private readonly IpcServer _server;
    private readonly IpcRouter _router;
    private readonly ModuleRunner _modules;
    private readonly CoreControl _control;
    private readonly IUiLauncher _launcher;
    private readonly IClientEvents _clients;
    private readonly ILogger<CoreHost> _logger;

    public CoreHost(
        ISettingsStore settings,
        StartupRegistrar startup,
        IpcServer server,
        IpcRouter router,
        ModuleRunner modules,
        CoreControl control,
        IUiLauncher launcher,
        IClientEvents clients,
        ILogger<CoreHost> logger)
    {
        _settings = settings;
        _startup = startup;
        _server = server;
        _router = router;
        _modules = modules;
        _control = control;
        _launcher = launcher;
        _clients = clients;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        _server.ConfigureHandler(async (envelope, connectionToken) =>
        {
            using var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                connectionToken,
                _control.Token);
            return await _router.HandleAsync(envelope, linkedToken.Token).ConfigureAwait(false);
        });
        var serverStarted = false;
        try
        {
            _server.Start();
            serverStarted = true;

            SynchronizeStartup();

            await _modules.StartAllAsync(_control.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "TrayX.Core ready with {Started}/{Total} modules",
                _modules.GetStatuses().Count(status => status.IsRunning),
                _modules.GetStatuses().Count);

            if (_settings.Current.OpenDashboardOnStartup)
            {
                OpenUi("dashboard");
            }

            await Task.Delay(Timeout.Infinite, _control.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_control.Token.IsCancellationRequested)
        {
        }
        finally
        {
            _logger.LogInformation("TrayX.Core is shutting down");
            try
            {
                if (serverStarted)
                {
                    await _server.PublishAsync(IpcEvents.CoreShutdown)
                        .WaitAsync(TimeSpan.FromSeconds(1))
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException exception)
            {
                _logger.LogWarning(exception, "Timed out delivering the Core shutdown event");
            }
            finally
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _modules.StopAllAsync(stopTimeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    await _server.StopAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private void OpenUi(string page)
    {
        if (_clients.ClientCount > 0)
        {
            _clients.Publish(IpcEvents.UiOpen, new UiOpenRequest(page));
            return;
        }

        _launcher.Open(page);
    }

    private void SynchronizeStartup()
    {
        var desired = _settings.Current.LaunchAtStartup;
        try
        {
            if (desired != _startup.IsEnabled())
            {
                _startup.SetEnabled(desired, CoreExecutablePath.Resolve());
                _logger.LogInformation("Autostart synchronized: {Enabled}", desired);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Autostart could not be synchronized");
        }
    }
}
