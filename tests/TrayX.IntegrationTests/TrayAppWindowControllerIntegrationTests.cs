using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using TrayX.Application.Hosting;
using TrayX.Application.Configuration;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Modules.TrayManager;
using TrayX.Storage.TrayApps;
using TrayX.Windows.Shell;

namespace TrayX.IntegrationTests;

public sealed class TrayAppWindowControllerIntegrationTests
{
    [Fact]
    public async Task HideAsync_UsesStableIdentityWhenTitleChangesAfterEnumeration()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var window = await TestWindowHost.CreateAsync();
        try
        {
            var catalog = new TrayAppWindowCatalog(includeCurrentProcess: true);
            var original = Assert.Single(catalog.GetVisibleWindows(), candidate => candidate.Title == window.Title);
            await window.SetTitleAsync(window.Title + " (changed)");
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var controller = new TrayAppWindowController(catalog, journal);

            Assert.True(await controller.HideAsync(original));
            Assert.False(await window.IsVisibleAsync());
            Assert.Equal(1, await controller.RestorePendingAsync());
            Assert.True(await window.IsVisibleAsync());
            Assert.Empty(await journal.GetPendingAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HideAsync_PersistsBeforeHide_AndRestorePendingShowsSameWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var window = await TestWindowHost.CreateAsync();
        try
        {
            var catalog = new TrayAppWindowCatalog(includeCurrentProcess: true);
            var windowDto = Assert.Single(catalog.GetVisibleWindows(), candidate => candidate.Title == window.Title);
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var controller = new TrayAppWindowController(catalog, journal);

            Assert.True(await controller.HideAsync(windowDto));
            Assert.False(await window.IsVisibleAsync());
            Assert.Single(await journal.GetPendingAsync());
            await window.SetTitleAsync(window.Title + " (changed)");

            Assert.Equal(1, await controller.RestorePendingAsync());
            Assert.True(await window.IsVisibleAsync());
            Assert.Empty(await journal.GetPendingAsync());

            var refreshedWindow = Assert.Single(
                catalog.GetVisibleWindows(),
                candidate => candidate.Title == window.Title + " (changed)");
            Assert.True(await controller.HideAsync(refreshedWindow, automatic: true));
            Assert.True(Assert.Single(await journal.GetPendingAsync()).IsAutomatic);
            Assert.Equal(0, await controller.RestoreManualPendingAsync());
            Assert.False(await window.IsVisibleAsync());

            Assert.Equal(1, await controller.RestoreAutomaticPendingAsync());
            Assert.True(await window.IsVisibleAsync());
            Assert.Empty(await journal.GetPendingAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RestoreWindowAsync_RestoresOnlyTheMatchingRecoveryEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var firstWindow = await TestWindowHost.CreateAsync();
        await using var secondWindow = await TestWindowHost.CreateAsync();
        try
        {
            var catalog = new TrayAppWindowCatalog(includeCurrentProcess: true);
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var controller = new TrayAppWindowController(catalog, journal);
            var firstDto = Assert.Single(
                catalog.GetVisibleWindows(),
                candidate => candidate.Title == firstWindow.Title);
            var secondDto = Assert.Single(
                catalog.GetVisibleWindows(),
                candidate => candidate.Title == secondWindow.Title);

            Assert.True(await controller.HideAsync(firstDto));
            Assert.True(await controller.HideAsync(secondDto));
            var records = await journal.GetPendingAsync();
            var firstRecord = Assert.Single(records, record => record.WindowTitle == firstWindow.Title);

            var request = new TrayAppRestoreWindowRequest(
                firstRecord.WindowHandle,
                firstRecord.ProcessId,
                firstRecord.ProcessStartTimeUtcTicks);
            Assert.Equal(1, await controller.RestoreWindowAsync(request));
            Assert.True(await firstWindow.IsVisibleAsync());
            Assert.False(await secondWindow.IsVisibleAsync());

            var remaining = Assert.Single(await journal.GetPendingAsync());
            Assert.Equal(secondWindow.Title, remaining.WindowTitle);
            Assert.Equal(0, await controller.RestoreWindowAsync(request));
            Assert.Single(await journal.GetPendingAsync());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TrayAppsModule_AutoHidesOnlyWithMasterAndRuleOptIn_AndRestoresOnlyAutomaticWindowsWhenDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var window = await TestWindowHost.CreateAsync();
        await using var manualWindow = await TestWindowHost.CreateAsync();
        TrayAppsModule? module = null;
        try
        {
            var settings = new TestSettingsStore(TrayXSettings.Default with
            {
                TrayAppsAutoHideEnabled = true,
                TrayAppRules =
                [
                    new TrayAppRuleSettings
                    {
                        Name = window.Title,
                        WindowTitle = window.Title,
                        MatchMode = TrayAppMatchMode.WindowTitle
                    }
                ]
            });
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            var catalog = new TrayAppWindowCatalog(includeCurrentProcess: true);
            var controller = new TrayAppWindowController(catalog, journal);
            module = new TrayAppsModule(
                catalog,
                controller,
                journal,
                new TestClientEvents(),
                settings,
                NullLogger<TrayAppsModule>.Instance);

            await module.StartAsync(CancellationToken.None);
            Assert.True(await window.IsVisibleAsync());
            Assert.Empty(await journal.GetPendingAsync());

            await settings.UpdateAsync(settings.Current with
            {
                TrayAppRules = settings.Current.TrayAppRules
                    .Select(rule => rule with { AutoHide = true })
                    .ToArray()
            });
            await WaitForHiddenAsync(window, journal);
            Assert.False(await window.IsVisibleAsync());
            Assert.True(Assert.Single(await journal.GetPendingAsync()).IsAutomatic);

            var manualWindowDto = Assert.Single(
                catalog.GetVisibleWindows(),
                candidate => candidate.Title == manualWindow.Title);
            Assert.True(await module.HideWindowAsync(
                manualWindowDto,
                automatic: false,
                CancellationToken.None));
            Assert.False(await manualWindow.IsVisibleAsync());

            await settings.UpdateAsync(settings.Current with { TrayAppsAutoHideEnabled = false });
            await WaitForAutomaticRestoreWithoutManualRestoreAsync(window, manualWindow, journal);
        }
        finally
        {
            if (module is not null)
            {
                await module.StopAsync(CancellationToken.None);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TrayAppsModule_UpdatesRecoverySnapshotAfterManualHideAndSingleRestore()
    {
        var root = Path.Combine(Path.GetTempPath(), "trayx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await using var window = await TestWindowHost.CreateAsync();
        TrayAppsModule? module = null;
        try
        {
            var catalog = new TrayAppWindowCatalog(includeCurrentProcess: true);
            var journal = new TrayAppRecoveryJournal(new TrayXPaths(root));
            module = new TrayAppsModule(
                catalog,
                new TrayAppWindowController(catalog, journal),
                journal,
                new TestClientEvents(),
                new TestSettingsStore(TrayXSettings.Default),
                NullLogger<TrayAppsModule>.Instance);
            var recoveryUpdates = new List<IReadOnlyList<TrayAppRecoveryRecord>>();
            module.PendingChanged += recoveryUpdates.Add;

            await module.StartAsync(CancellationToken.None);
            Assert.Empty(module.GetPendingSnapshot());

            var windowDto = Assert.Single(
                catalog.GetVisibleWindows(),
                candidate => candidate.Title == window.Title);
            Assert.True(await module.HideWindowAsync(
                windowDto,
                automatic: false,
                CancellationToken.None));

            var recovery = Assert.Single(module.GetPendingSnapshot());
            Assert.Equal(window.Title, recovery.WindowTitle);
            Assert.Collection(
                recoveryUpdates,
                update => Assert.Equal(window.Title, Assert.Single(update).WindowTitle));
            Assert.Equal(1, await module.RestoreWindowAsync(
                new TrayAppRestoreWindowRequest(
                    recovery.WindowHandle,
                    recovery.ProcessId,
                    recovery.ProcessStartTimeUtcTicks),
                CancellationToken.None));

            Assert.True(await window.IsVisibleAsync());
            Assert.Empty(module.GetPendingSnapshot());
            Assert.Empty(Assert.Single(recoveryUpdates.Skip(1)));
        }
        finally
        {
            if (module is not null)
            {
                await module.StopAsync(CancellationToken.None);
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task WaitForAutomaticRestoreWithoutManualRestoreAsync(
        TestWindowHost automaticWindow,
        TestWindowHost manualWindow,
        TrayAppRecoveryJournal journal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var pending = await journal.GetPendingAsync();
            if (await automaticWindow.IsVisibleAsync() &&
                !await manualWindow.IsVisibleAsync() &&
                pending.Count == 1 &&
                !pending[0].IsAutomatic)
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(await automaticWindow.IsVisibleAsync());
        Assert.False(await manualWindow.IsVisibleAsync());
        Assert.Collection(
            await journal.GetPendingAsync(),
            record => Assert.False(record.IsAutomatic));
    }

    private static async Task WaitForHiddenAsync(
        TestWindowHost window,
        TrayAppRecoveryJournal journal)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (!await window.IsVisibleAsync() &&
                (await journal.GetPendingAsync()).Any(record => record.IsAutomatic))
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.False(await window.IsVisibleAsync());
        Assert.Contains(await journal.GetPendingAsync(), record => record.IsAutomatic);
    }

    private sealed class TestSettingsStore(TrayXSettings current) : ISettingsStore
    {
        public TrayXSettings Current { get; private set; } = current;

        public event EventHandler<TrayXSettings>? Changed;

        public Task UpdateAsync(TrayXSettings settings, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = settings.Normalize();
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(
            Func<TrayXSettings, TrayXSettings> update,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Current = update(Current).Normalize();
            Changed?.Invoke(this, Current);
            return Task.CompletedTask;
        }

        public Task ReloadAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class TestClientEvents : IClientEvents
    {
        public int ClientCount => 0;

        public void Publish(string name, object? payload = null)
        {
        }
    }

    private sealed class TestWindowHost : IAsyncDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<Dispatcher> _dispatcherReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Window? _window;

        private TestWindowHost(string title)
        {
            Title = title;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "TrayX window integration test"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public string Title { get; }

        public static async Task<TestWindowHost> CreateAsync()
        {
            var host = new TestWindowHost($"TrayX test window {Guid.NewGuid():N}");
            _ = await host._dispatcherReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return host;
        }

        public Task<bool> IsVisibleAsync() =>
            GetDispatcher().InvokeAsync(() =>
                _window is not null &&
                IsWindowVisible(new WindowInteropHelper(_window).Handle)).Task;

        public Task SetTitleAsync(string title) =>
            GetDispatcher().InvokeAsync(() =>
            {
                if (_window is null)
                {
                    throw new InvalidOperationException("The test window is not initialized.");
                }

                _window.Title = title;
            }).Task;

        public ValueTask DisposeAsync()
        {
            var dispatcher = GetDispatcher();
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The test window dispatcher did not stop.");
            }

            return ValueTask.CompletedTask;
        }

        private Dispatcher GetDispatcher() => _dispatcherReady.Task.GetAwaiter().GetResult();

        private void Run()
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                _window = new Window
                {
                    Title = Title,
                    Width = 320,
                    Height = 180,
                    Left = 20,
                    Top = 20,
                    ShowActivated = false,
                    ShowInTaskbar = true,
                    WindowStartupLocation = WindowStartupLocation.Manual
                };
                _window.Show();
                _dispatcherReady.TrySetResult(dispatcher);
                Dispatcher.Run();
                _window.Close();
            }
            catch (Exception exception)
            {
                _dispatcherReady.TrySetException(exception);
            }
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);
    }
}
