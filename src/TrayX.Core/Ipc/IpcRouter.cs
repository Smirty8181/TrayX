using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Core.Hosting;
using TrayX.Modules.AppLauncher;
using TrayX.Modules.AudioCenter;
using TrayX.Modules.Automation;
using TrayX.Modules.MultiMonitor;
using TrayX.Modules.QuickSettings;
using TrayX.Modules.SystemMonitor;
using TrayX.Modules.SoundEvents;
using TrayX.Modules.TrayManager;
using TrayX.Windows;

namespace TrayX.Core.Ipc;

public sealed class IpcRouter
{
    private readonly IClientEvents _events;
    private readonly ISettingsStore _settings;
    private readonly ModuleRunner _modules;
    private readonly CoreRuntimeInfo _info;
    private readonly ICoreControl _core;
    private readonly AudioCenterModule _audio;
    private readonly MultiMonitorModule _displays;
    private readonly SystemMonitorModule _system;
    private readonly TrayManagerModule _tray;
    private readonly TrayAppsModule _trayApps;
    private readonly AppLauncherModule _apps;
    private readonly ProfilesModule _profiles;
    private readonly NotificationHistoryModule _notifications;
    private readonly SoundEventsModule _soundEvents;
    private readonly QuickSettingsModule _quick;
    private readonly TrayX.Windows.Startup.StartupRegistrar _startup;
    private readonly ILogger<IpcRouter> _logger;

    public IpcRouter(
        IClientEvents events,
        ISettingsStore settings,
        ModuleRunner modules,
        CoreRuntimeInfo info,
        ICoreControl core,
        AudioCenterModule audio,
        MultiMonitorModule displays,
        SystemMonitorModule system,
        TrayManagerModule tray,
        TrayAppsModule trayApps,
        AppLauncherModule apps,
        ProfilesModule profiles,
        NotificationHistoryModule notifications,
        SoundEventsModule soundEvents,
        QuickSettingsModule quick,
        TrayX.Windows.Startup.StartupRegistrar startup,
        ILogger<IpcRouter> logger)
    {
        _events = events;
        _settings = settings;
        _modules = modules;
        _info = info;
        _core = core;
        _audio = audio;
        _displays = displays;
        _system = system;
        _tray = tray;
        _trayApps = trayApps;
        _apps = apps;
        _profiles = profiles;
        _notifications = notifications;
        _soundEvents = soundEvents;
        _quick = quick;
        _startup = startup;
        _logger = logger;
    }

    public async Task<IpcEnvelope> HandleAsync(IpcEnvelope request, CancellationToken cancellationToken)
    {
        if (request.Type != IpcMessageTypes.Request ||
            request.Id is not { } requestId ||
            string.IsNullOrWhiteSpace(request.Name))
        {
            return IpcEnvelope.Failure(request.Id ?? Guid.NewGuid(), "Некорректный IPC-запрос.");
        }

        try
        {
            var result = await DispatchAsync(request.Name, request.Payload, cancellationToken)
                .ConfigureAwait(false);
            return IpcEnvelope.Response(requestId, result);
        }
        catch (OperationCanceledException)
        {
            return IpcEnvelope.Failure(requestId, "Операция отменена.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "IPC command {Command} failed", request.Name);
            return IpcEnvelope.Failure(requestId, GetSafeFailureMessage());
        }
    }

    private async Task<object?> DispatchAsync(string command, System.Text.Json.JsonElement? payload, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case IpcCommands.StatusGet:
                return BuildStatus();

            case IpcCommands.SettingsGet:
                return _settings.Current;

            case IpcCommands.SettingsUpdate:
            {
                var requested = Payload<TrayXSettings>(payload);
                TrayXSettingsJson.EnsureSupportedVersion(requested);
                var updated = requested.Normalize();
                await _settings.UpdateAsync(current =>
                {
                    if (updated.LaunchAtStartup != current.LaunchAtStartup)
                    {
                        _startup.SetEnabled(updated.LaunchAtStartup, CoreExecutablePath.Resolve());
                    }

                    return updated;
                }, cancellationToken).ConfigureAwait(false);
                _events.Publish(IpcEvents.SettingsChanged, updated);
                return updated;
            }

            case IpcCommands.AudioGetState:
                return _audio.Service.GetState();

            case IpcCommands.AudioGetInputPeak:
                return _audio.Service.GetDefaultInputPeak();

            case IpcCommands.AudioSetVolume:
            {
                var request = Payload<SetVolumeRequest>(payload);
                var level = _audio.Service.SetMasterVolume(request.Volume);
                _events.Publish(IpcEvents.AudioChanged, new AudioChangedEvent(level.Volume, level.Muted));
                return level;
            }

            case IpcCommands.AudioSetMute:
            {
                var request = Payload<SetMuteRequest>(payload);
                var level = _audio.Service.SetMasterMute(request.Muted);
                _events.Publish(IpcEvents.AudioChanged, new AudioChangedEvent(level.Volume, level.Muted));
                return level;
            }

            case IpcCommands.AudioDeviceSetVolume:
            {
                var request = Payload<AudioDeviceVolumeRequest>(payload);
                var level = _audio.Service.SetDeviceVolume(request.DeviceId, request.Volume);
                return level;
            }

            case IpcCommands.AudioDeviceSetMute:
            {
                var request = Payload<AudioDeviceMuteRequest>(payload);
                var level = _audio.Service.SetDeviceMute(request.DeviceId, request.Muted);
                return level;
            }

            case IpcCommands.AudioDeviceSetDefault:
            {
                var request = Payload<AudioDeviceDefaultRequest>(payload);
                var changed = _audio.Service.SetDefaultDevice(request.DeviceId);
                if (changed)
                {
                    _events.Publish(IpcEvents.AudioDevicesChanged);
                }

                return changed;
            }

            case IpcCommands.AudioSetSessionVolume:
            {
                var request = Payload<SessionVolumeRequest>(payload);
                return _audio.Service.SetSessionVolume(request.Key, request.Volume);
            }

            case IpcCommands.AudioSetSessionMute:
            {
                var request = Payload<SessionMuteRequest>(payload);
                return _audio.Service.SetSessionMute(request.Key, request.Muted);
            }

            case IpcCommands.DisplaysGetAll:
                return _displays.GetDisplays();

            case IpcCommands.DisplaysSetBrightness:
            {
                var request = Payload<BrightnessRequest>(payload);
                var displays = _displays.SetBrightness(request.DisplayIndex, request.Percent, request.StableId);
                return displays;
            }

            case IpcCommands.DisplaysOpenSettings:
                TrayX.Windows.Display.DisplayService.OpenWindowsDisplaySettings();
                return null;

            case IpcCommands.SystemGetStats:
                return _system.GetStats();

            case IpcCommands.TrayGetIcons:
                return _tray.GetIcons();

            case IpcCommands.TraySetPromoted:
                return _tray.SetPromoted(Payload<TrayPromoteRequest>(payload));

            case IpcCommands.TrayRestartExplorer:
                _tray.RestartExplorer();
                return null;

            case IpcCommands.TrayLiveGet:
                return _tray.GetSnapshot();

            case IpcCommands.TrayLiveActivate:
                return _tray.ActivateIcon(Payload<TrayIconRequest>(payload));

            case IpcCommands.TrayLiveContextMenu:
                return _tray.ShowIconContextMenu(Payload<TrayIconRequest>(payload));

            case IpcCommands.TrayOverflowToggle:
                return _tray.ToggleOverflow();

            case IpcCommands.TrayIconImage:
                return _tray.GetIconImage(Payload<TrayIconKeyRequest>(payload));

            case IpcCommands.TrayAppsGetWindows:
                cancellationToken.ThrowIfCancellationRequested();
                return _trayApps.GetVisibleWindows();

            case IpcCommands.TrayAppsGetPending:
                return await _trayApps.GetPendingAsync(cancellationToken).ConfigureAwait(false);

            case IpcCommands.TrayAppsHideWindow:
                return await _trayApps.HideWindowAsync(
                    Payload<TrayAppHideWindowRequest>(payload).Window,
                    automatic: false,
                    cancellationToken).ConfigureAwait(false);

            case IpcCommands.TrayAppsRestoreWindow:
                return await _trayApps.RestoreWindowAsync(
                    Payload<TrayAppRestoreWindowRequest>(payload),
                    cancellationToken).ConfigureAwait(false);

            case IpcCommands.TrayAppsRestorePending:
                return await _trayApps.RestorePendingAsync(cancellationToken).ConfigureAwait(false);

            case IpcCommands.AppsGetAll:
                return await _apps.GetCatalogAsync(cancellationToken).ConfigureAwait(false);

            case IpcCommands.AppsLaunch:
                await _apps.LaunchAsync(Payload<LaunchAppRequest>(payload), cancellationToken).ConfigureAwait(false);
                return null;

            case IpcCommands.ProfilesGetAll:
                return await _profiles.GetAllAsync(cancellationToken).ConfigureAwait(false);

            case IpcCommands.ProfilesSave:
            {
                var request = Payload<SaveProfileRequest>(payload);
                return await _profiles.SaveCurrentAsync(request.Name, cancellationToken).ConfigureAwait(false);
            }

            case IpcCommands.ProfilesApply:
            {
                var request = Payload<ProfileIdRequest>(payload);
                return await _profiles.ApplyAsync(request.Id, cancellationToken).ConfigureAwait(false);
            }

            case IpcCommands.ProfilesDelete:
            {
                var request = Payload<ProfileIdRequest>(payload);
                await _profiles.DeleteAsync(request.Id, cancellationToken).ConfigureAwait(false);
                return null;
            }

            case IpcCommands.NotificationsGetRecent:
                return await _notifications.GetRecentAsync(cancellationToken).ConfigureAwait(false);

            case IpcCommands.NotificationsClear:
                await _notifications.ClearAsync(cancellationToken).ConfigureAwait(false);
                return null;

            case IpcCommands.SoundPreview:
                return _soundEvents.PreviewSound(Payload<SoundPreviewRequest>(payload).Path);

            case IpcCommands.WindowsThemeGet:
                return _quick.GetWindowsTheme();

            case IpcCommands.WindowsThemeSet:
            {
                var request = Payload<WindowsThemeRequest>(payload);
                var theme = _quick.SetWindowsTheme(request.Mode);
                _events.Publish(IpcEvents.WindowsThemeChanged, theme);
                return theme;
            }

            case IpcCommands.WindowsSettingsOpen:
            {
                var request = Payload<SettingsPageRequest>(payload);
                _quick.OpenSettingsPage(request.Page);
                return null;
            }

            case IpcCommands.WindowsClassicSoundOpen:
                _quick.OpenClassicSoundControlPanel();
                return null;

            case IpcCommands.UiActivate:
            {
                var request = Payload<UiActivateRequest>(payload);
                var page = string.IsNullOrWhiteSpace(request.Page) ? "dashboard" : request.Page;
                _events.Publish(IpcEvents.UiActivate, new UiOpenRequest(page));
                return null;
            }

            case IpcCommands.UiPing:
                return new { pong = true, protocol = TrayXIpc.ProtocolVersion };

            case IpcCommands.CoreShutdown:
                _events.Publish(IpcEvents.CoreShutdown);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(150).ConfigureAwait(false);
                    _core.RequestShutdown();
                });
                return null;

            default:
                throw new InvalidOperationException($"Неизвестная команда: {command}");
        }
    }

    private CoreStatusDto BuildStatus()
    {
        return new CoreStatusDto(
            _info.Version,
            TrayXIpc.ProtocolVersion,
            _info.StartedAt,
            _info.WindowsBuild,
            _info.UserName,
            _modules.GetStatuses());
    }

    private static T Payload<T>(System.Text.Json.JsonElement? payload)
    {
        var value = IpcJson.FromElement<T>(payload);
        if (value is null)
        {
            throw new InvalidOperationException("Некорректные параметры команды.");
        }

        return value;
    }

    private string GetSafeFailureMessage() =>
        string.Equals(_settings.Current.Language, "en", StringComparison.OrdinalIgnoreCase)
            ? "The IPC command failed. See the TrayX log for details."
            : "Команда IPC завершилась ошибкой. Подробности записаны в журнал TrayX.";
}
