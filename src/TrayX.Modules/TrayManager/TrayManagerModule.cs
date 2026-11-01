using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Audio;
using TrayX.Windows.Display;
using TrayX.Windows.Shell;
using TrayX.Windows.Themes;

namespace TrayX.Modules.TrayManager;

public sealed class TrayManagerModule : ITrayXModule
{
    private readonly ISettingsStore _settings;
    private readonly AudioCenterService _audio;
    private readonly WindowsThemeService _themes;
    private readonly TrayIconSettingsService _iconSettings;
    private readonly DisplayService _display;
    private readonly TrayRuntimeService _runtime;
    private readonly ExplorerService _explorer;
    private readonly IClientEvents _clients;
    private readonly IUiLauncher _launcher;
    private readonly ICoreControl _core;
    private readonly TrayAppsModule _trayApps;
    private readonly ILogger<TrayManagerModule> _logger;

    private TrayIconHost? _host;
    private TrayPanelHost? _panels;
    private CancellationTokenSource? _poll;
    private Task? _pollTask;
    private readonly TraySnapshotSignatureTracker _signatureTracker = new();
    private long _snapshotSequence;

    public TrayManagerModule(
        ISettingsStore settings,
        AudioCenterService audio,
        WindowsThemeService themes,
        TrayIconSettingsService iconSettings,
        DisplayService display,
        TrayRuntimeService runtime,
        ExplorerService explorer,
        IClientEvents clients,
        IUiLauncher launcher,
        ICoreControl core,
        TrayAppsModule trayApps,
        ILogger<TrayManagerModule> logger)
    {
        _settings = settings;
        _audio = audio;
        _themes = themes;
        _iconSettings = iconSettings;
        _display = display;
        _runtime = runtime;
        _explorer = explorer;
        _clients = clients;
        _launcher = launcher;
        _core = core;
        _trayApps = trayApps;
        _logger = logger;
    }

    public string Id => "tray";

    public string DisplayName => "TrayX · значок и меню";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var iconPath = Path.Combine(AppContext.BaseDirectory, "trayx.ico");
            _host = new TrayIconHost(
                iconPath,
                Tooltip(_settings.Current.Language),
                HandleMenuCommand,
                HandleTrayAppRecovery,
                BuildMenuState,
                HandleLeftClick,
                message => _logger.LogWarning("{TrayMessage}", message));

            _host.Start(_settings.Current.TrayIconEnabled);
            cancellationToken.ThrowIfCancellationRequested();
            _panels = new TrayPanelHost(
                _display,
                _iconSettings,
                id => ActivateIcon(new TrayIconRequest(id)),
                id => ShowIconContextMenu(new TrayIconRequest(id)),
                OpenUi,
                record => _ = RestoreTrayAppWindowAsync(record),
                ToggleOverflow,
                exception => _logger.LogWarning(exception, "Monitor tray panel operation failed"));
            _panels.Start(_settings.Current);
            cancellationToken.ThrowIfCancellationRequested();
            _panels.UpdateSnapshot(TakeSnapshot());
            _trayApps.PendingChanged += OnPendingTrayAppsChanged;
            _panels.UpdateHiddenWindows(_trayApps.GetPendingSnapshot());
            _settings.Changed += OnSettingsChanged;
            var poll = new CancellationTokenSource();
            _poll = poll;
            _pollTask = Task.Run(() => PollLiveAsync(poll.Token), CancellationToken.None);
            _logger.LogInformation("Tray icon host started");
        }
        catch (Exception startupException)
        {
            try
            {
                await StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupException)
            {
                throw new AggregateException(
                    "Tray Runtime startup failed and cleanup was incomplete.",
                    startupException,
                    cleanupException);
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        _trayApps.PendingChanged -= OnPendingTrayAppsChanged;
        List<Exception>? failures = null;
        _poll?.Cancel();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!_pollTask.IsCompleted)
                {
                    throw new TimeoutException("Tray live polling did not stop before the module shutdown deadline.");
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Tray live polling failed during shutdown");
                (failures ??= []).Add(exception);
            }
        }

        _poll?.Dispose();
        _poll = null;
        _pollTask = null;

        try
        {
            _panels?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            _panels = null;
        }

        try
        {
            _host?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            _host = null;
        }

        if (failures is not null)
        {
            throw new AggregateException("Tray Runtime did not stop cleanly.", failures);
        }

        return;
    }

    public TraySnapshotDto GetSnapshot()
    {
        var sequence = Interlocked.Increment(ref _snapshotSequence);
        var snapshot = TakeSnapshot();
        _signatureTracker.TryUpdate(sequence, Signature(snapshot));
        return snapshot;
    }

    private TraySnapshotDto TakeSnapshot() => _runtime.Snapshot(_settings.Current.TrayLiveEnabled);

    public bool ActivateIcon(TrayIconRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var activated = _runtime.Activate(request.Id);
        _logger.LogInformation("Tray activate {Id} result {Result}", request.Id, activated);
        PublishSnapshotIfChanged();
        return activated;
    }

    public bool ShowIconContextMenu(TrayIconRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shown = _runtime.ShowContextMenu(request.Id);
        _logger.LogInformation("Tray context menu {Id} result {Result}", request.Id, shown);
        PublishSnapshotIfChanged();
        return shown;
    }

    public bool ToggleOverflow()
    {
        var toggled = _runtime.ToggleOverflow();
        PublishSnapshotIfChanged();
        return toggled;
    }

    public TrayIconImageDto GetIconImage(TrayIconKeyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new TrayIconImageDto(request.Key, _runtime.IconImage(request.Key));
    }

    private async Task PollLiveAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var interval = Math.Clamp(_settings.Current.TrayLivePollMs, 1000, 30000);
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_settings.Current.TrayLiveEnabled ||
                (_clients.ClientCount == 0 && _panels?.HasPanels != true))
            {
                continue;
            }

            try
            {
                var sequence = Interlocked.Increment(ref _snapshotSequence);
                var snapshot = TakeSnapshot();
                _panels?.UpdateSnapshot(snapshot);
                var signature = Signature(snapshot);
                if (_signatureTracker.TryUpdate(sequence, signature))
                {
                    _clients.Publish(IpcEvents.TrayLiveChanged, snapshot);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Tray live scan failed");
            }
        }
    }

    private void PublishSnapshotIfChanged()
    {
        try
        {
            var sequence = Interlocked.Increment(ref _snapshotSequence);
            var snapshot = TakeSnapshot();
            _panels?.UpdateSnapshot(snapshot);
            var signature = Signature(snapshot);
            if (!_signatureTracker.TryUpdate(sequence, signature))
            {
                return;
            }

            _clients.Publish(IpcEvents.TrayLiveChanged, snapshot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray snapshot publish failed");
        }
    }

    private static string Signature(TraySnapshotDto snapshot) =>
        System.Text.Json.JsonSerializer.Serialize(snapshot with { ScannedAt = default });

    public IReadOnlyList<TrayIconDto> GetIcons() => _iconSettings.GetIcons();

    public TrayIconDto SetPromoted(TrayPromoteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var icon = _iconSettings.SetPromoted(request.Key, request.Promoted);
        _clients.Publish(IpcEvents.TrayChanged);
        _logger.LogInformation(
            "Tray icon {Name} promoted={Promoted}",
            icon.Name,
            icon.IsPromoted);
        return icon;
    }

    public void RestartExplorer()
    {
        _logger.LogInformation("Explorer restart requested by user");
        _explorer.Restart();
        _clients.Publish(IpcEvents.TrayChanged);
        Notify(TrayManagerMessage.ExplorerRestarted);
    }

    private void OnSettingsChanged(object? sender, TrayXSettings settings)
    {
        _host?.SetEnabled(settings.TrayIconEnabled);
        _host?.UpdateTooltip(Tooltip(settings.Language));
        _panels?.UpdateSettings(settings);
    }

    private void HandleLeftClick()
    {
        switch (_settings.Current.TrayLeftClickAction)
        {
            case "dashboard":
                OpenUi("dashboard");
                break;
            case "mute":
                ToggleMute();
                break;
            default:
                OpenUi("panel");
                break;
        }
    }

    private void HandleMenuCommand(TrayMenuCommand command)
    {
        switch (command)
        {
            case TrayMenuCommand.Open:
                OpenUi("dashboard");
                break;
            case TrayMenuCommand.QuickPanel:
                OpenUi("panel");
                break;
            case TrayMenuCommand.Audio:
                OpenUi("audio");
                break;
            case TrayMenuCommand.Settings:
                OpenUi("settings");
                break;
            case TrayMenuCommand.ToggleMute:
                ToggleMute();
                break;
            case TrayMenuCommand.ToggleTheme:
                ToggleTheme();
                break;
            case TrayMenuCommand.ToggleOverflow:
                if (!_runtime.ToggleOverflow())
                {
                    Notify(TrayManagerMessage.OverflowFailed);
                }

                PublishSnapshotIfChanged();
                break;
            case TrayMenuCommand.TrayApps:
                OpenUi("tray-apps");
                break;
            case TrayMenuCommand.RestoreTrayApps:
                _ = RestoreTrayAppWindowsAsync();
                break;
            case TrayMenuCommand.Exit:
                _core.RequestShutdown();
                break;
        }
    }

    private async Task RestoreTrayAppWindowsAsync()
    {
        try
        {
            var restored = await _trayApps.RestorePendingAsync(CancellationToken.None).ConfigureAwait(false);
            var pending = await _trayApps.GetPendingAsync(CancellationToken.None).ConfigureAwait(false);
            Notify(TrayManagerMessage.RecoverySummary, restored, pending.Count);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray app recovery from tray menu failed");
            Notify(TrayManagerMessage.RecoveryFailed);
        }
    }

    private void HandleTrayAppRecovery(TrayAppRecoveryRecord record) =>
        _ = RestoreTrayAppWindowAsync(record);

    private async Task RestoreTrayAppWindowAsync(TrayAppRecoveryRecord record)
    {
        try
        {
            var restored = await _trayApps.RestoreWindowAsync(
                new TrayAppRestoreWindowRequest(
                    record.WindowHandle,
                    record.ProcessId,
                    record.ProcessStartTimeUtcTicks),
                CancellationToken.None).ConfigureAwait(false);
            var title = string.IsNullOrWhiteSpace(record.WindowTitle)
                ? record.WindowClass
                : record.WindowTitle;
            Notify(
                restored > 0 ? TrayManagerMessage.WindowRestored : TrayManagerMessage.WindowRestoreFailed,
                title);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray app recovery from tray submenu failed");
            Notify(TrayManagerMessage.RecoveryFailed);
        }
    }

    private void OnPendingTrayAppsChanged(IReadOnlyList<TrayAppRecoveryRecord> pending) =>
        _panels?.UpdateHiddenWindows(pending);

    private void ToggleMute()
    {
        try
        {
            var current = _audio.GetMasterLevel();
            var level = _audio.SetMasterMute(!current.Muted);
            _clients.Publish(IpcEvents.AudioChanged, new AudioChangedEvent(level.Volume, level.Muted));
            Notify(level.Muted ? TrayManagerMessage.AudioMuted : TrayManagerMessage.AudioUnmuted);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray mute toggle failed");
            Notify(TrayManagerMessage.AudioToggleFailed);
        }
    }

    private void ToggleTheme()
    {
        try
        {
            _themes.Toggle();
            var mode = _themes.GetMode();
            Notify(mode == "dark"
                ? TrayManagerMessage.ThemeChangedDark
                : TrayManagerMessage.ThemeChangedLight);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Windows theme toggle failed");
            Notify(TrayManagerMessage.ThemeChangeFailed);
        }
    }

    private TrayMenuState BuildMenuState()
    {
        var level = _audio.GetMasterLevel();
        return new TrayMenuState(
            level.Muted,
            _themes.GetMode() == "dark",
            _settings.Current.Language,
            _clients.ClientCount > 0,
            _runtime.IsOverflowOpen,
            _trayApps.GetPendingSnapshot());
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

    private void Notify(TrayManagerMessage message, params object?[] arguments)
    {
        var (title, text) = TrayManagerMessages.Format(_settings.Current.Language, message, arguments);
        _clients.Publish(IpcEvents.Toast, new ToastEvent(title, text));
        if (_settings.Current.ShowTrayNotifications)
        {
            _host?.ShowNotification(title, text);
        }
    }

    private static string Tooltip(string language) =>
        string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
            ? "TrayX — Windows control"
            : "TrayX — управление Windows";
}
