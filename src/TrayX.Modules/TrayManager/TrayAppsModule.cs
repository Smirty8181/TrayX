using Microsoft.Extensions.Logging;
using System.Threading.Channels;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Settings;
using TrayX.Application.TrayApps;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Shell;

namespace TrayX.Modules.TrayManager;

public sealed class TrayAppsModule : ITrayXModule
{
    private readonly TrayAppWindowCatalog _windows;
    private readonly TrayAppWindowController _controller;
    private readonly ITrayAppRecoveryJournal _journal;
    private readonly IClientEvents _clients;
    private readonly ISettingsStore _settings;
    private readonly ILogger<TrayAppsModule> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly Channel<byte> _reconcileRequests = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
    private TrayAppWindowEventMonitor? _windowEvents;
    private Task? _reconcileTask;
    private CancellationTokenSource? _reconcileLifetime;
    private TrayAppRecoveryRecord[] _pendingSnapshot = [];

    public TrayAppsModule(
        TrayAppWindowCatalog windows,
        TrayAppWindowController controller,
        ITrayAppRecoveryJournal journal,
        IClientEvents clients,
        ISettingsStore settings,
        ILogger<TrayAppsModule> logger)
    {
        _windows = windows;
        _controller = controller;
        _journal = journal;
        _clients = clients;
        _settings = settings;
        _logger = logger;
    }

    public string Id => "tray-apps";

    public string DisplayName => "Приложения в трей";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public event Action<IReadOnlyList<TrayAppRecoveryRecord>>? PendingChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pending = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        var restored = await _controller.RestoreManualPendingAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Tray app recovery completed: {Restored} manual windows restored; {Pending} records were pending",
            restored,
            pending.Count);
        _windowEvents = new TrayAppWindowEventMonitor(
            () =>
            {
                _clients.Publish(IpcEvents.TrayAppsWindowsChanged);
                ScheduleReconcile();
            },
            exception => _logger.LogWarning(exception, "Tray app window event monitor failed"));
        _windowEvents.Start();
        _settings.Changed += OnSettingsChanged;
        var reconcileLifetime = new CancellationTokenSource();
        _reconcileLifetime = reconcileLifetime;
        _reconcileTask = Task.Run(() => ProcessReconcileRequestsAsync(reconcileLifetime.Token));
        ScheduleReconcile();
        _logger.LogInformation("Tray app window event monitor started");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        List<Exception>? failures = null;
        try
        {
            _windowEvents?.Dispose();
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            _windowEvents = null;
        }

        _reconcileLifetime?.Cancel();
        var reconcileTask = _reconcileTask;
        try
        {
            if (reconcileTask is not null)
            {
                await reconcileTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                _reconcileTask = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (reconcileTask is not null && !reconcileTask.IsCompleted)
            {
                (failures ??= []).Add(
                    new TimeoutException("Tray app rule reconciliation did not stop before shutdown."));
            }
            else
            {
                _reconcileTask = null;
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }
        finally
        {
            if (reconcileTask is null || reconcileTask.IsCompleted)
            {
                _reconcileLifetime?.Dispose();
                _reconcileLifetime = null;
            }
        }

        try
        {
            var restored = await _controller.RestorePendingAsync(cancellationToken).ConfigureAwait(false);
            await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (restored > 0)
            {
                _logger.LogInformation("Restored {WindowCount} tray app windows during shutdown", restored);
            }
        }
        catch (Exception exception)
        {
            (failures ??= []).Add(exception);
        }

        if (failures is not null)
        {
            throw new AggregateException("Tray app window module did not stop cleanly.", failures);
        }
    }

    public IReadOnlyList<TrayAppWindowDto> GetVisibleWindows() => _windows.GetVisibleWindows();

    public IReadOnlyList<TrayAppRecoveryRecord> GetPendingSnapshot() =>
        Volatile.Read(ref _pendingSnapshot).ToArray();

    public async Task<IReadOnlyList<TrayAppRecoveryRecord>> GetPendingAsync(CancellationToken cancellationToken)
    {
        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return GetPendingSnapshot();
    }

    public async Task<bool> HideWindowAsync(
        TrayAppWindowDto window,
        bool automatic,
        CancellationToken cancellationToken)
    {
        var hidden = await _controller.HideAsync(window, automatic, cancellationToken).ConfigureAwait(false);
        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return hidden;
    }

    public async Task<int> RestorePendingAsync(CancellationToken cancellationToken)
    {
        var restored = await _controller.RestorePendingAsync(cancellationToken).ConfigureAwait(false);
        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return restored;
    }

    public async Task<int> RestoreWindowAsync(
        TrayAppRestoreWindowRequest request,
        CancellationToken cancellationToken)
    {
        var restored = await _controller.RestoreWindowAsync(request, cancellationToken).ConfigureAwait(false);
        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return restored;
    }

    private void OnSettingsChanged(object? sender, TrayXSettings settings) => ScheduleReconcile();

    private void ScheduleReconcile()
    {
        _ = _reconcileRequests.Writer.TryWrite(0);
    }

    private async Task ProcessReconcileRequestsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in _reconcileRequests.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await ReconcileAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Tray app automatic rule reconciliation failed");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!settings.TrayAppsAutoHideEnabled)
        {
            var restored = await _controller.RestoreAutomaticPendingAsync(cancellationToken)
                .ConfigureAwait(false);
            if (restored > 0)
            {
                _logger.LogInformation(
                    "Automatic tray app hiding disabled; restored {WindowCount} windows",
                    restored);
            }

            await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var pending = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in pending.Where(record => record.IsAutomatic))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string processName;
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(record.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks != record.ProcessStartTimeUtcTicks)
                {
                    _ = await _controller.RestoreAutomaticRecordAsync(record, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                processName = process.ProcessName + ".exe";
            }
            catch (ArgumentException)
            {
                _ = await _controller.RestoreAutomaticRecordAsync(record, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                continue;
            }

            var currentMatch = TrayAppRuleMatcher.FindMatch(
                settings.TrayAppRules,
                new TrayAppWindowInfo(processName, record.WindowTitle, record.WindowClass),
                exception => _logger.LogWarning(
                    exception,
                    "Tray app rule regex timed out while reconciling window {WindowHandle}",
                    record.WindowHandle));
            if (currentMatch is null || currentMatch.IsExclusion || !currentMatch.AutoHide)
            {
                _ = await _controller.RestoreAutomaticRecordAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var currentPending = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        var pendingIdentities = currentPending
            .Select(record => (record.WindowHandle, record.ProcessId, record.ProcessStartTimeUtcTicks))
            .ToHashSet();
        foreach (var window in _windows.GetVisibleWindows())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pendingIdentities.Any(identity =>
                    identity.WindowHandle == window.Handle && identity.ProcessId == window.ProcessId))
            {
                continue;
            }

            settings = _settings.Current;
            if (!settings.TrayAppsAutoHideEnabled)
            {
                return;
            }

            var match = TrayAppRuleMatcher.FindMatch(
                settings.TrayAppRules,
                new TrayAppWindowInfo(window.ProcessName, window.Title, window.WindowClass),
                exception => _logger.LogWarning(
                    exception,
                    "Tray app rule regex timed out while matching window {WindowHandle}",
                    window.Handle));
            if (match is null || match.IsExclusion || !match.AutoHide)
            {
                continue;
            }

            if (await _controller.HideAsync(
                    window,
                    automatic: true,
                    cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "Automatically hid window {WindowHandle} for tray app rule {RuleId}",
                    window.Handle,
                    match.Id);
            }
        }

        await RefreshPendingSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RefreshPendingSnapshotAsync(CancellationToken cancellationToken)
    {
        var pending = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = pending.ToArray();
        var previous = Volatile.Read(ref _pendingSnapshot);
        Volatile.Write(ref _pendingSnapshot, snapshot);
        if (previous.SequenceEqual(snapshot))
        {
            return;
        }

        try
        {
            PendingChanged?.Invoke(snapshot);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Tray app recovery list update subscriber failed");
        }
    }
}
