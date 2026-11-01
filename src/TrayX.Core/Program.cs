using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TrayX.Application.Configuration;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Notifications;
using TrayX.Application.Profiles;
using TrayX.Application.Settings;
using TrayX.Application.TrayApps;
using TrayX.Contracts;
using TrayX.Core.Hosting;
using TrayX.Core.Ipc;
using TrayX.Modules.AppLauncher;
using TrayX.Modules.AudioCenter;
using TrayX.Modules.Automation;
using TrayX.Modules.MultiMonitor;
using TrayX.Modules.QuickSettings;
using TrayX.Modules.SoundEvents;
using TrayX.Modules.SystemMonitor;
using TrayX.Modules.TrayManager;
using TrayX.Storage.Profiles;
using TrayX.Storage.Notifications;
using TrayX.Storage.Settings;
using TrayX.Storage.TrayApps;
using TrayX.Windows;
using TrayX.Windows.Apps;
using TrayX.Windows.Audio;
using TrayX.Windows.Display;
using TrayX.Windows.Shell;
using TrayX.Windows.Startup;
using TrayX.Windows.SystemInfo;
using TrayX.Windows.Themes;

var paths = TrayXPaths.ForCurrentUser();
paths.EnsureCreated();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(paths.LogsDirectory, "core-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    if (!WindowsSupport.IsSupported)
    {
        Log.Fatal(
            "TrayX.Core requires Windows 11 build {MinimumBuild} or newer.",
            WindowsSupport.MinimumBuild);
        return 2;
    }

    Log.Information(
        "TrayX.Core starting, data directory {DataDirectory}, protocol {ProtocolVersion}",
        paths.RootDirectory,
        TrayXIpc.ProtocolVersion);

    Mutex? mutex = null;
    var ownsMutex = false;
    try
    {
        mutex = new Mutex(false, TrayXIpc.GetMutexName("Core"));
        try
        {
            ownsMutex = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            Log.Information("Another TrayX.Core instance is active, activating its UI");
            var activated = await ActivateExistingCoreAsync();
            return activated ? 0 : 3;
        }

        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(Log.Logger);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton<SettingsStore>();
        builder.Services.AddSingleton<ISettingsStore>(provider => provider.GetRequiredService<SettingsStore>());
        builder.Services.AddSingleton<ProfileStore>();
        builder.Services.AddSingleton<IProfileStore>(provider => provider.GetRequiredService<ProfileStore>());
        builder.Services.AddSingleton<NotificationStore>();
        builder.Services.AddSingleton<INotificationStore>(provider =>
            provider.GetRequiredService<NotificationStore>());
        builder.Services.AddSingleton<TrayAppRecoveryJournal>();
        builder.Services.AddSingleton<ITrayAppRecoveryJournal>(provider =>
            provider.GetRequiredService<TrayAppRecoveryJournal>());

        builder.Services.AddSingleton<AudioCenterService>();
        builder.Services.AddSingleton<DisplayService>();
        builder.Services.AddSingleton<SystemMetricsService>();
        builder.Services.AddSingleton<WindowsThemeService>();
        builder.Services.AddSingleton<TrayIconSettingsService>();
        builder.Services.AddSingleton<TrayLiveService>();
        builder.Services.AddSingleton<TrayRuntimeService>();
        builder.Services.AddSingleton<TrayAppWindowCatalog>();
        builder.Services.AddSingleton<TrayAppWindowController>();
        builder.Services.AddSingleton<ExplorerService>();
        builder.Services.AddSingleton<AppCatalogService>();
        builder.Services.AddSingleton(_ => new StartupRegistrar("TrayX"));

        builder.Services.AddSingleton(provider => new IpcServer(
            TrayXIpc.GetPipeName(),
            provider.GetRequiredService<ILogger<IpcServer>>()));
        builder.Services.AddSingleton<NotificationHistoryModule>();
        builder.Services.AddSingleton<IClientEvents>(provider =>
            provider.GetRequiredService<NotificationHistoryModule>());
        builder.Services.AddSingleton<IpcRouter>();

        builder.Services.AddSingleton<CoreRuntimeInfo>();
        builder.Services.AddSingleton<CoreControl>();
        builder.Services.AddSingleton<ICoreControl>(provider => provider.GetRequiredService<CoreControl>());
        builder.Services.AddSingleton<IUiLauncher, UiLauncher>();

        builder.Services.AddSingleton<ITrayXModule>(provider =>
            provider.GetRequiredService<NotificationHistoryModule>());
        builder.Services.AddSingleton<AudioCenterModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<AudioCenterModule>());
        builder.Services.AddSingleton<MultiMonitorModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<MultiMonitorModule>());
        builder.Services.AddSingleton<SystemMonitorModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<SystemMonitorModule>());
        builder.Services.AddSingleton<QuickSettingsModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<QuickSettingsModule>());
        builder.Services.AddSingleton<AppLauncherModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<AppLauncherModule>());
        builder.Services.AddSingleton<SoundEventsModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<SoundEventsModule>());
        builder.Services.AddSingleton<TrayManagerModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<TrayManagerModule>());
        builder.Services.AddSingleton<TrayAppsModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<TrayAppsModule>());
        builder.Services.AddSingleton<ProfilesModule>();
        builder.Services.AddSingleton<ITrayXModule>(provider => provider.GetRequiredService<ProfilesModule>());

        builder.Services.AddSingleton<ModuleRunner>();
        builder.Services.AddSingleton<CoreHost>();

        using var host = builder.Build();
        var control = host.Services.GetRequiredService<CoreControl>();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            control.RequestShutdown();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => control.RequestShutdown();

        var core = host.Services.GetRequiredService<CoreHost>();
        await core.RunAsync();
        return 0;
    }
    finally
    {
        if (ownsMutex)
        {
            try
            {
                mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        mutex?.Dispose();
    }
}
catch (Exception exception)
{
    Log.Fatal(exception, "TrayX.Core stopped unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<bool> ActivateExistingCoreAsync()
{
    for (var attempt = 0; attempt < 12; attempt++)
    {
        await using var client = new IpcClient();
        try
        {
            await client.ConnectAsync(TimeSpan.FromSeconds(1));
            await client.SendAsync(
                IpcCommands.UiActivate,
                new UiActivateRequest("dashboard"),
                TimeSpan.FromSeconds(3));
            return true;
        }
        catch (Exception exception) when (exception is TimeoutException or IpcException or IOException)
        {
            if (attempt == 11)
            {
                Log.Warning(exception, "The existing TrayX.Core did not accept an activation request");
            }
        }

        await Task.Delay(250);
    }

    return false;
}
