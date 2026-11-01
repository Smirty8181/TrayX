using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;
using TrayX.UI.ViewModels;
using TrayX.UI.Views;

namespace TrayX.UI;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private bool _shuttingDown;

    private UiContext? _context;
    private DialogService? _dialogs;
    private MainWindowViewModel? _mainViewModel;
    private MainWindow? _mainWindow;
    private QuickPanelViewModel? _quickViewModel;
    private QuickPanelWindow? _quickWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        UiExceptionLog.Initialize();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);

        try
        {
            await StartAsync(e.Args).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            UiExceptionLog.Write("Startup", exception.ToString());
            MessageBox.Show(
                exception.ToString(),
                "TrayX",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_ownsMutex)
            {
                _instanceMutex?.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
        }

        _instanceMutex?.Dispose();
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        UiExceptionLog.Write("Dispatcher", e.Exception.ToString());
        e.Handled = true;
        MessageBox.Show(
            "В интерфейсе произошла непредвиденная ошибка. Подробности записаны в журнал TrayX.",
            "TrayX",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        Shutdown(1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        UiExceptionLog.Write(
            "Process",
            $"IsTerminating={e.IsTerminating}; {e.ExceptionObject}");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        UiExceptionLog.Write("UnobservedTask", e.Exception.ToString());
        e.SetObserved();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _shuttingDown = true;
        _mainWindow?.ForceClose();
        _quickWindow?.ForceClose();
        base.OnSessionEnding(e);
    }

    private async Task StartAsync(string[] args)
    {
        var page = ParsePage(args);

        _instanceMutex = new Mutex(false, TrayXIpc.GetMutexName("Ui"));
        try
        {
            _ownsMutex = _instanceMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            await ActivateExistingInstanceAsync(page).ConfigureAwait(true);
            Shutdown();
            return;
        }

        _context = new UiContext();
        await _context.StartAsync().ConfigureAwait(true);

        if (!_context.IsConnected && await TryStartCoreAsync().ConfigureAwait(true))
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!_context.IsConnected && DateTime.UtcNow < deadline)
            {
                if (await _context.ReconnectAsync().ConfigureAwait(true))
                {
                    break;
                }

                await Task.Delay(350).ConfigureAwait(true);
            }
        }

        ApplyUiSettings(_context.Settings);
        _dialogs = new DialogService();
        _mainViewModel = new MainWindowViewModel(_context, _dialogs);

        _mainWindow = new MainWindow
        {
            DataContext = _mainViewModel
        };

        MainWindow = _mainWindow;

        _mainViewModel.QuickPanelRequested += (_, _) => ShowQuickPanel();
        _mainViewModel.ExitRequested += (_, _) => _ = ShutdownAsync();
        _context.NavigationRequested += (_, target) => HandleNavigation(target);
        _context.ShutdownRequested += (_, _) => _ = ShutdownAsync(requestCore: false);
        _context.SettingsChanged += (_, settings) => ApplyUiSettings(settings);

        await _mainViewModel.InitializeAsync().ConfigureAwait(true);

        if (string.Equals(page, "panel", StringComparison.OrdinalIgnoreCase))
        {
            ShowQuickPanel();
        }
        else
        {
            ShowMain(page);
        }
    }

    private void ApplyUiSettings(TrayXSettings settings)
    {
        var normalized = settings.Normalize();
        var windowsDark = _context?.WindowsTheme?.Mode is { } mode
            ? string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase)
            : ThemeManager.GetWindowsDark();

        Loc.Instance.SetLanguage(normalized.Language);
        ThemeManager.Apply(normalized, windowsDark);
    }

    private void HandleNavigation(string page)
    {
        if (string.Equals(page, "panel", StringComparison.OrdinalIgnoreCase))
        {
            ShowQuickPanel();
            return;
        }

        ShowMain(page);
    }

    private void ShowMain(string page)
    {
        if (_mainWindow is null || _mainViewModel is null)
        {
            return;
        }

        HideQuickPanel();

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainViewModel.Navigate(page);
        _mainWindow.Activate();
    }

    private void ShowQuickPanel()
    {
        if (_context is null)
        {
            return;
        }

        EnsureQuickWindow();
        if (_quickWindow is null)
        {
            return;
        }

        _quickWindow.ShowPanel();
        _ = _quickViewModel!.OpenAsync();
    }

    private void HideQuickPanel()
    {
        _quickWindow?.Hide();
    }

    private void EnsureQuickWindow()
    {
        if (_quickWindow is not null || _context is null)
        {
            return;
        }

        _quickViewModel = new QuickPanelViewModel(_context);
        _quickWindow = new QuickPanelWindow
        {
            DataContext = _quickViewModel
        };

        _quickViewModel.NavigationRequested += (_, page) =>
        {
            HideQuickPanel();
            ShowMain(page);
        };
        _quickViewModel.CloseRequested += (_, _) => HideQuickPanel();
        _quickWindow.IsVisibleChanged += (_, _) =>
        {
            if (_quickWindow.IsVisible)
            {
                return;
            }

            _quickViewModel.Deactivate();
        };
    }

    private async Task ShutdownAsync(bool requestCore = true)
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;

        try
        {
            if (requestCore && _context is not null)
            {
                await _context.RequestShutdownAsync()
                    .WaitAsync(TimeSpan.FromSeconds(4))
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is TimeoutException or IpcException)
        {
        }

        _quickWindow?.ForceClose();
        _mainWindow?.ForceClose();

        if (_context is not null)
        {
            await _context.DisposeAsync().ConfigureAwait(true);
        }

        Shutdown();
    }

    private static async Task ActivateExistingInstanceAsync(string page)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await using var client = new IpcClient();
            try
            {
                await client.ConnectAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await client.SendAsync(
                    IpcCommands.UiActivate,
                    new UiActivateRequest(page),
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (
                exception is TimeoutException or IpcException or IOException or OperationCanceledException)
            {
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    private static async Task<bool> TryStartCoreAsync()
    {
        var executable = LocateCoreExecutable();
        if (executable is null)
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
            });
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }

        await Task.Delay(600).ConfigureAwait(true);
        return true;
    }

    private static string? LocateCoreExecutable()
    {
        var besideUi = Path.Combine(AppContext.BaseDirectory, "TrayX.Core.exe");
        if (File.Exists(besideUi))
        {
            return besideUi;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 10 && directory is not null; depth++)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrayX.sln")))
            {
                var coreBin = Path.Combine(directory.FullName, "src", "TrayX.Core", "bin");
                if (!Directory.Exists(coreBin))
                {
                    return null;
                }

                try
                {
                    return Directory
                        .EnumerateFiles(coreBin, "TrayX.Core.exe", SearchOption.AllDirectories)
                        .Where(path => !path.Contains(
                            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(path => path.Contains(
                            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static string ParsePage(string[] args)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--page", StringComparison.OrdinalIgnoreCase))
            {
                var page = args[index + 1].Trim().ToLowerInvariant();
                return page.Length == 0 ? "dashboard" : page;
            }
        }

        return "dashboard";
    }
}
