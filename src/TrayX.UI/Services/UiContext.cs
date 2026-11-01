using System.Windows;
using System.Windows.Threading;
using TrayX.Contracts;
using TrayX.UI.Localization;
using System.IO;

namespace TrayX.UI.Services;

public sealed class UiContext : IAsyncDisposable
{
    private readonly IpcClient _client = new();
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    private int _reconnecting;
    private bool _started;
    private bool _disposed;

    public UiContext()
    {
        _dispatcher = Application.Current.Dispatcher;
        _client.EventReceived += OnEventReceived;
        _client.Disconnected += OnDisconnected;
    }

    public event EventHandler? ConnectionChanged;

    public event EventHandler<TrayXSettings>? SettingsChanged;

    public event EventHandler<AudioChangedEvent>? AudioChanged;

    public event EventHandler? AudioDevicesChanged;

    public event EventHandler? AudioSessionsChanged;

    public event EventHandler<WindowsThemeDto>? WindowsThemeChanged;

    public event EventHandler<ToastEvent>? ToastReceived;

    public event EventHandler? NotificationHistoryChanged;

    public event EventHandler<string>? NavigationRequested;

    public event EventHandler<string>? DeviceChanged;

    public event EventHandler<ProfileDto>? ProfileApplied;

    public event EventHandler? TrayChanged;

    public event EventHandler<TraySnapshotDto>? TrayLiveChanged;

    public event EventHandler? TrayAppsWindowsChanged;

    public event EventHandler? ShutdownRequested;

    public event EventHandler<string>? Message;

    public TrayXSettings Settings { get; private set; } = TrayXSettings.Default;

    public CoreStatusDto? CoreStatus { get; private set; }

    public WindowsThemeDto? WindowsTheme { get; private set; }

    public bool IsConnected => _client.IsConnected;

    public string? LastError { get; private set; }

    public async Task StartAsync()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        var connected = await TryConnectAsync().ConfigureAwait(true);
        if (!connected)
        {
            StartReconnectLoop();
        }
    }

    public async Task<bool> ReconnectAsync()
    {
        var connected = await TryConnectAsync().ConfigureAwait(true);
        if (!connected)
        {
            StartReconnectLoop();
        }

        return connected;
    }

    public void Report(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        OnUi(() => Message?.Invoke(this, message));
    }

    public async Task<TrayXSettings?> ReloadSettingsAsync()
    {
        var settings = await GetAsync<TrayXSettings>(IpcCommands.SettingsGet).ConfigureAwait(true);
        if (settings is not null)
        {
            ApplySettings(settings);
        }

        return settings;
    }

    public async Task<bool> UpdateSettingsAsync(TrayXSettings settings)
    {
        var updated = await GetAsync<TrayXSettings>(IpcCommands.SettingsUpdate, settings).ConfigureAwait(true);
        if (updated is null)
        {
            return false;
        }

        ApplySettings(updated);
        return true;
    }

    public Task<CoreStatusDto?> GetStatusAsync(bool silent = false) =>
        GetAsync<CoreStatusDto>(IpcCommands.StatusGet, silent: silent);

    public Task<AudioStateDto?> GetAudioStateAsync(bool silent = false) =>
        GetAsync<AudioStateDto>(IpcCommands.AudioGetState, silent: silent);

    public Task<AudioPeakDto?> GetDefaultInputPeakAsync() =>
        GetAsync<AudioPeakDto?>(IpcCommands.AudioGetInputPeak, silent: true);

    public Task<AudioLevelDto?> SetMasterVolumeAsync(double volume) =>
        GetAsync<AudioLevelDto>(IpcCommands.AudioSetVolume, new SetVolumeRequest(volume));

    public Task<AudioLevelDto?> SetMasterMuteAsync(bool muted) =>
        GetAsync<AudioLevelDto>(IpcCommands.AudioSetMute, new SetMuteRequest(muted));

    public Task<AudioLevelDto?> SetAudioDeviceVolumeAsync(string deviceId, double volume) =>
        GetAsync<AudioLevelDto>(
            IpcCommands.AudioDeviceSetVolume,
            new AudioDeviceVolumeRequest(deviceId, volume));

    public Task<AudioLevelDto?> SetAudioDeviceMuteAsync(string deviceId, bool muted) =>
        GetAsync<AudioLevelDto>(
            IpcCommands.AudioDeviceSetMute,
            new AudioDeviceMuteRequest(deviceId, muted));

    public Task<bool?> SetDefaultAudioDeviceAsync(string deviceId) =>
        GetAsync<bool?>(
            IpcCommands.AudioDeviceSetDefault,
            new AudioDeviceDefaultRequest(deviceId));

    public Task<AudioLevelDto?> SetSessionVolumeAsync(string key, double volume) =>
        GetAsync<AudioLevelDto>(IpcCommands.AudioSetSessionVolume, new SessionVolumeRequest(key, volume));

    public Task<AudioLevelDto?> SetSessionMuteAsync(string key, bool muted) =>
        GetAsync<AudioLevelDto>(IpcCommands.AudioSetSessionMute, new SessionMuteRequest(key, muted));

    public Task<List<DisplayDto>?> GetDisplaysAsync(bool silent = false) =>
        GetAsync<List<DisplayDto>>(IpcCommands.DisplaysGetAll, silent: silent);

    public Task<List<DisplayDto>?> SetBrightnessAsync(int displayIndex, int percent, string stableId = "") =>
        GetAsync<List<DisplayDto>>(
            IpcCommands.DisplaysSetBrightness,
            new BrightnessRequest(displayIndex, percent, stableId));

    public Task<bool> OpenDisplaySettingsAsync() =>
        ExecAsync(IpcCommands.DisplaysOpenSettings);

    public Task<SystemStatsDto?> GetStatsAsync(bool silent = false) =>
        GetAsync<SystemStatsDto>(IpcCommands.SystemGetStats, silent: silent);

    public Task<List<TrayIconDto>?> GetTrayIconsAsync(bool silent = false) =>
        GetAsync<List<TrayIconDto>>(IpcCommands.TrayGetIcons, silent: silent);

    public Task<TrayIconDto?> SetTrayPromotedAsync(string key, bool promoted) =>
        GetAsync<TrayIconDto>(IpcCommands.TraySetPromoted, new TrayPromoteRequest(key, promoted));

    public Task<bool> RestartExplorerAsync() =>
        ExecAsync(IpcCommands.TrayRestartExplorer);

    public Task<TraySnapshotDto?> GetTraySnapshotAsync(bool silent = false) =>
        GetAsync<TraySnapshotDto>(IpcCommands.TrayLiveGet, silent: silent);

    public Task<bool> ActivateTrayIconAsync(string id) =>
        GetAsync<bool>(IpcCommands.TrayLiveActivate, new TrayIconRequest(id));

    public Task<bool> ShowTrayIconContextMenuAsync(string id) =>
        GetAsync<bool>(IpcCommands.TrayLiveContextMenu, new TrayIconRequest(id));

    public Task<bool> ToggleTrayOverflowAsync() =>
        GetAsync<bool>(IpcCommands.TrayOverflowToggle);

    public Task<TrayIconImageDto?> GetTrayIconImageAsync(string key) =>
        GetAsync<TrayIconImageDto>(IpcCommands.TrayIconImage, new TrayIconKeyRequest(key));

    public Task<List<AppEntryDto>?> GetAppsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<AppEntryDto>>(IpcCommands.AppsGetAll, cancellationToken: cancellationToken);

    public Task<bool> LaunchAppAsync(AppEntryDto app) =>
        ExecAsync(
            IpcCommands.AppsLaunch,
            new LaunchAppRequest(app.Id, app.ShortcutPath, app.Source));

    public Task<List<ProfileDto>?> GetProfilesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<List<ProfileDto>>(IpcCommands.ProfilesGetAll, cancellationToken: cancellationToken);

    public Task<ProfileDto?> SaveProfileAsync(string name) =>
        GetAsync<ProfileDto>(IpcCommands.ProfilesSave, new SaveProfileRequest(name));

    public Task<ProfileDto?> ApplyProfileAsync(Guid id) =>
        GetAsync<ProfileDto>(IpcCommands.ProfilesApply, new ProfileIdRequest(id));

    public Task<bool> DeleteProfileAsync(Guid id) =>
        ExecAsync(IpcCommands.ProfilesDelete, new ProfileIdRequest(id));

    public Task<List<NotificationDto>?> GetRecentNotificationsAsync() =>
        GetAsync<List<NotificationDto>>(IpcCommands.NotificationsGetRecent);

    public Task<List<TrayAppWindowDto>?> GetVisibleTrayAppWindowsAsync() =>
        GetAsync<List<TrayAppWindowDto>>(IpcCommands.TrayAppsGetWindows);

    public Task<List<TrayAppRecoveryRecord>?> GetPendingTrayAppRecoveriesAsync() =>
        GetAsync<List<TrayAppRecoveryRecord>>(IpcCommands.TrayAppsGetPending);

    public Task<bool> HideTrayAppWindowAsync(TrayAppWindowDto window) =>
        GetAsync<bool>(IpcCommands.TrayAppsHideWindow, new TrayAppHideWindowRequest(window));

    public Task<int> RestoreTrayAppWindowAsync(TrayAppRecoveryRecord record) =>
        GetAsync<int>(
            IpcCommands.TrayAppsRestoreWindow,
            new TrayAppRestoreWindowRequest(
                record.WindowHandle,
                record.ProcessId,
                record.ProcessStartTimeUtcTicks));

    public Task<int> RestorePendingTrayAppWindowsAsync() =>
        GetAsync<int>(IpcCommands.TrayAppsRestorePending);

    public Task<bool> ClearNotificationHistoryAsync() =>
        ExecAsync(IpcCommands.NotificationsClear);

    public Task<bool> PreviewDeviceSoundAsync(string path) =>
        GetAsync<bool>(IpcCommands.SoundPreview, new SoundPreviewRequest(path));

    public Task<WindowsThemeDto?> GetWindowsThemeAsync(bool silent = false) =>
        GetAsync<WindowsThemeDto>(IpcCommands.WindowsThemeGet, silent: silent);

    public async Task<WindowsThemeDto?> SetWindowsThemeAsync(string mode)
    {
        var theme = await GetAsync<WindowsThemeDto>(
            IpcCommands.WindowsThemeSet,
            new WindowsThemeRequest(mode)).ConfigureAwait(true);

        if (theme is not null)
        {
            WindowsTheme = theme;
            WindowsThemeChanged?.Invoke(this, theme);
        }

        return theme;
    }

    public Task<bool> OpenWindowsSettingsAsync(string page) =>
        ExecAsync(IpcCommands.WindowsSettingsOpen, new SettingsPageRequest(page));

    public Task<bool> OpenClassicSoundControlPanelAsync() =>
        ExecAsync(IpcCommands.WindowsClassicSoundOpen);

    public async Task<bool> RequestShutdownAsync()
    {
        var result = await ExecAsync(IpcCommands.CoreShutdown).ConfigureAwait(true);
        return result;
    }

    private async Task<bool> TryConnectAsync()
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(8));

        await _connectGate.WaitAsync(linked.Token).ConfigureAwait(true);
        try
        {
            if (_client.IsConnected)
            {
                return true;
            }

            LastError = null;
            await _client.ConnectAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(true);
            await LoadInitialStateAsync(linked.Token).ConfigureAwait(true);
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, Settings);
            return true;
        }
        catch (Exception exception) when (
            exception is TimeoutException or IpcException or IOException or OperationCanceledException)
        {
            LastError = exception.Message;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task LoadInitialStateAsync(CancellationToken cancellationToken)
    {
        var settings = await _client.SendAsync<TrayXSettings>(
            IpcCommands.SettingsGet,
            null,
            TimeSpan.FromSeconds(4),
            cancellationToken).ConfigureAwait(true);

        if (settings is not null)
        {
            Settings = settings.Normalize();
        }

        try
        {
            CoreStatus = await _client.SendAsync<CoreStatusDto>(
                IpcCommands.StatusGet,
                null,
                TimeSpan.FromSeconds(4),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IpcException or TimeoutException or IOException)
        {
            CoreStatus = null;
        }

        try
        {
            WindowsTheme = await _client.SendAsync<WindowsThemeDto>(
                IpcCommands.WindowsThemeGet,
                null,
                TimeSpan.FromSeconds(4),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is IpcException or TimeoutException or IOException)
        {
            WindowsTheme = null;
        }
    }

    private async Task<T?> GetAsync<T>(
        string command,
        object? payload = null,
        CancellationToken cancellationToken = default,
        bool silent = false)
    {
        if (!_client.IsConnected)
        {
            if (!silent)
            {
                Report(Loc.T("Error_CoreOffline"));
            }

            return default;
        }

        try
        {
            return await _client.SendAsync<T>(
                command,
                payload,
                TimeSpan.FromSeconds(15),
                cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (
            exception is IpcException or TimeoutException or IOException or InvalidOperationException)
        {
            if (!silent)
            {
                Report(exception.Message);
            }

            return default;
        }
    }

    private async Task<bool> ExecAsync(string command, object? payload = null)
    {
        if (!_client.IsConnected)
        {
            Report(Loc.T("Error_CoreOffline"));
            return false;
        }

        try
        {
            await _client.SendAsync(command, payload, TimeSpan.FromSeconds(20)).ConfigureAwait(true);
            return true;
        }
        catch (Exception exception) when (
            exception is IpcException or TimeoutException or IOException or InvalidOperationException)
        {
            Report(exception.Message);
            return false;
        }
    }

    private void ApplySettings(TrayXSettings settings)
    {
        Settings = settings.Normalize();
        SettingsChanged?.Invoke(this, Settings);
    }

    private void OnEventReceived(object? sender, IpcEnvelope envelope)
    {
        if (envelope.Type != IpcMessageTypes.Event || string.IsNullOrWhiteSpace(envelope.Name))
        {
            return;
        }

        switch (envelope.Name)
        {
            case IpcEvents.SettingsChanged:
            {
                var settings = IpcJson.FromElement<TrayXSettings>(envelope.Payload);
                if (settings is not null)
                {
                    OnUi(() => ApplySettings(settings));
                }

                break;
            }

            case IpcEvents.AudioChanged:
            {
                var changed = IpcJson.FromElement<AudioChangedEvent>(envelope.Payload);
                if (changed is not null)
                {
                    OnUi(() => AudioChanged?.Invoke(this, changed));
                }

                break;
            }

            case IpcEvents.AudioDevicesChanged:
                OnUi(() => AudioDevicesChanged?.Invoke(this, EventArgs.Empty));
                break;

            case IpcEvents.AudioSessionsChanged:
                OnUi(() => AudioSessionsChanged?.Invoke(this, EventArgs.Empty));
                break;

            case IpcEvents.WindowsThemeChanged:
            {
                var theme = IpcJson.FromElement<WindowsThemeDto>(envelope.Payload);
                if (theme is not null)
                {
                    OnUi(() =>
                    {
                        WindowsTheme = theme;
                        WindowsThemeChanged?.Invoke(this, theme);
                    });
                }

                break;
            }

            case IpcEvents.UiOpen:
            case IpcEvents.UiActivate:
            {
                var request = IpcJson.FromElement<UiOpenRequest>(envelope.Payload);
                var page = string.IsNullOrWhiteSpace(request?.Page) ? "dashboard" : request!.Page;
                OnUi(() => NavigationRequested?.Invoke(this, page));
                break;
            }

            case IpcEvents.Toast:
            {
                var toast = IpcJson.FromElement<ToastEvent>(envelope.Payload);
                if (toast is not null)
                {
                    OnUi(() =>
                    {
                        ToastReceived?.Invoke(this, toast);
                        Message?.Invoke(this, $"{toast.Title}: {toast.Message}");
                    });
                }

                break;
            }

            case IpcEvents.NotificationHistoryChanged:
                OnUi(() => NotificationHistoryChanged?.Invoke(this, EventArgs.Empty));
                break;

            case IpcEvents.AudioDeviceChanged:
            {
                var device = IpcJson.FromElement<DeviceChangedEvent>(envelope.Payload);
                if (!string.IsNullOrWhiteSpace(device?.DeviceName))
                {
                    OnUi(() => DeviceChanged?.Invoke(this, device!.DeviceName));
                }

                break;
            }

            case IpcEvents.ProfileApplied:
            {
                var profile = IpcJson.FromElement<ProfileDto>(envelope.Payload);
                if (profile is not null)
                {
                    OnUi(() => ProfileApplied?.Invoke(this, profile));
                }

                break;
            }

            case IpcEvents.TrayChanged:
                OnUi(() => TrayChanged?.Invoke(this, EventArgs.Empty));
                break;

            case IpcEvents.TrayLiveChanged:
            {
                var snapshot = IpcJson.FromElement<TraySnapshotDto>(envelope.Payload);
                if (snapshot is not null)
                {
                    OnUi(() => TrayLiveChanged?.Invoke(this, snapshot));
                }

                break;
            }

            case IpcEvents.TrayAppsWindowsChanged:
                OnUi(() => TrayAppsWindowsChanged?.Invoke(this, EventArgs.Empty));
                break;

            case IpcEvents.CoreShutdown:
                OnUi(() => ShutdownRequested?.Invoke(this, EventArgs.Empty));
                break;
        }
    }

    private void OnDisconnected(object? sender, EventArgs e)
    {
        OnUi(() => ConnectionChanged?.Invoke(this, EventArgs.Empty));
        StartReconnectLoop();
    }

    private void StartReconnectLoop()
    {
        if (!_started || _disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                while (!_lifetime.IsCancellationRequested && !_client.IsConnected)
                {
                    await Task.Delay(1500, _lifetime.Token).ConfigureAwait(false);
                    try
                    {
                        var connected = await TryConnectAsync().ConfigureAwait(false);
                        if (connected)
                        {
                            break;
                        }
                    }
                    catch (Exception exception) when (
                        exception is OperationCanceledException or TimeoutException or IpcException or IOException)
                    {
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
                if (!_client.IsConnected && !_lifetime.IsCancellationRequested && !_disposed)
                {
                    StartReconnectLoop();
                }
            }
        });
    }

    private void OnUi(Action action)
    {
        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _client.EventReceived -= OnEventReceived;
        _client.Disconnected -= OnDisconnected;
        await _client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _connectGate.Dispose();
    }
}
