using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayX.Contracts;
using TrayX.Windows.Display;

namespace TrayX.Windows.Shell;

public sealed class TrayPanelHost : IDisposable
{
    private readonly DisplayService _displays;
    private readonly TrayIconSettingsService _iconSettings;
    private readonly Func<string, bool> _activateIcon;
    private readonly Func<string, bool> _showContextMenu;
    private readonly Action<string> _openUi;
    private readonly Action<TrayAppRecoveryRecord> _restoreTrayAppWindow;
    private readonly Func<bool> _toggleOverflow;
    private readonly Action<Exception> _onError;
    private readonly ConcurrentQueue<Action> _pending = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Dictionary<string, TrayMonitorPanelWindow> _panels =
        new(StringComparer.OrdinalIgnoreCase);

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _reconcileTimer;
    private TrayXSettings _settings = TrayXSettings.Default;
    private TraySnapshotDto? _snapshot;
    private IReadOnlyList<TrayAppRecoveryRecord> _pendingTrayApps = Array.Empty<TrayAppRecoveryRecord>();
    private Exception? _startupFailure;
    private volatile bool _disposed;
    private int _panelCount;
    private int _reconcileInProgress;
    private int _reconcileRequested;

    public TrayPanelHost(
        DisplayService displays,
        TrayIconSettingsService iconSettings,
        Func<string, bool> activateIcon,
        Func<string, bool> showContextMenu,
        Action<string> openUi,
        Action<TrayAppRecoveryRecord> restoreTrayAppWindow,
        Func<bool> toggleOverflow,
        Action<Exception> onError)
    {
        _displays = displays;
        _iconSettings = iconSettings;
        _activateIcon = activateIcon;
        _showContextMenu = showContextMenu;
        _openUi = openUi;
        _restoreTrayAppWindow = restoreTrayAppWindow;
        _toggleOverflow = toggleOverflow;
        _onError = onError;
    }

    public bool HasPanels => Volatile.Read(ref _panelCount) > 0;

    public void Start(TrayXSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _settings = settings.Normalize();
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TrayX.MonitorPanels"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(8)))
        {
            throw new TimeoutException("Не удалось запустить панели TrayX для мониторов.");
        }

        if (_startupFailure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    public void UpdateSettings(TrayXSettings settings) =>
        Post(() =>
        {
            _settings = settings.Normalize();
            RequestPanelReconcile();
            foreach (var panel in _panels.Values)
            {
                panel.UpdateLanguage(_settings.Language);
                panel.UpdateClockFormat(_settings.ClockFormat);
                panel.UpdateAppearance(_settings.ThemeMode, _settings.Accent);
            }

            if (!_settings.TrayLiveEnabled)
            {
                var empty = new TraySnapshotDto(
                    DateTimeOffset.Now,
                    false,
                    false,
                    TrayNotices.LiveDisabled,
                    Array.Empty<TrayLiveIconDto>());
                foreach (var panel in _panels.Values)
                {
                    panel.UpdateIcons(empty);
                }
            }
        });

    public void UpdateSnapshot(TraySnapshotDto snapshot) =>
        Post(() =>
        {
            _snapshot = snapshot;
            foreach (var panel in _panels.Values)
            {
                panel.UpdateIcons(snapshot);
            }
        });

    public void UpdateHiddenWindows(IReadOnlyList<TrayAppRecoveryRecord> pending) =>
        Post(() =>
        {
            _pendingTrayApps = pending.ToArray();
            foreach (var panel in _panels.Values)
            {
                panel.UpdateHiddenWindows(_pendingTrayApps);
            }
        });

    private void Run()
    {
        try
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _reconcileTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
                (_, _) => RequestPanelReconcile(), _dispatcher);
            ReconcilePanels(_displays.GetDisplays());
            _ready.Set();
            Dispatcher.Run();
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _ready.Set();
        }
        finally
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            foreach (var panel in _panels.Values)
            {
                panel.Close();
            }

            _panels.Clear();
            Volatile.Write(ref _panelCount, 0);
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        RequestPanelReconcile();

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            RequestPanelReconcile();
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_settings.ThemeMode != "system")
        {
            return;
        }

        Post(() =>
        {
            foreach (var panel in _panels.Values)
            {
                panel.UpdateAppearance(_settings.ThemeMode, _settings.Accent);
            }
        });
    }

    private void RequestPanelReconcile()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _reconcileRequested, 1);
        if (Interlocked.Exchange(ref _reconcileInProgress, 1) != 0)
        {
            return;
        }

        _ = RefreshDisplaysAsync();
    }

    private async Task RefreshDisplaysAsync()
    {
        Interlocked.Exchange(ref _reconcileRequested, 0);
        IReadOnlyList<DisplayDto> displays;
        try
        {
            displays = await Task.Run(_displays.GetDisplays).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Post(() =>
            {
                try
                {
                    _onError(exception);
                }
                finally
                {
                    CompletePanelReconcile();
                }
            });
            return;
        }

        Post(() =>
        {
            try
            {
                if (!_disposed)
                {
                    RunSafely(() => ReconcilePanels(displays));
                }
            }
            finally
            {
                CompletePanelReconcile();
            }
        });
    }

    private void CompletePanelReconcile()
    {
        Volatile.Write(ref _reconcileInProgress, 0);
        if (!_disposed && Volatile.Read(ref _reconcileRequested) != 0)
        {
            RequestPanelReconcile();
        }
    }

    private void ReconcilePanels(IReadOnlyList<DisplayDto> displays)
    {
        var disabled = _settings.DisabledTrayPanelDisplays.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var desired = displays
            .Where(display =>
                !display.IsPrimary &&
                !disabled.Contains(display.StableId) &&
                !disabled.Contains(display.DeviceName))
            .ToDictionary(DisplayIdentity, StringComparer.OrdinalIgnoreCase);

        foreach (var key in _panels.Keys.Where(key => !desired.ContainsKey(key)).ToArray())
        {
            _panels[key].Close();
            _panels.Remove(key);
        }

        foreach (var (displayId, display) in desired)
        {
            if (!_panels.TryGetValue(displayId, out var panel))
            {
                panel = new TrayMonitorPanelWindow(
                    display,
                    _activateIcon,
                    _showContextMenu,
                    _iconSettings,
                    _openUi,
                    _restoreTrayAppWindow,
                    _toggleOverflow,
                    _onError,
                    _settings);
                _panels.Add(displayId, panel);
                panel.Show();
                if (_snapshot is not null)
                {
                    panel.UpdateIcons(_snapshot);
                }

                panel.UpdateHiddenWindows(_pendingTrayApps);
            }
            else
            {
                panel.UpdateDisplay(display);
            }
        }

        Volatile.Write(ref _panelCount, _panels.Count);
    }

    private static string DisplayIdentity(DisplayDto display) =>
        string.IsNullOrWhiteSpace(display.StableId) ? display.DeviceName : display.StableId;

    private void Post(Action action)
    {
        if (_disposed)
        {
            return;
        }

        var dispatcher = _dispatcher;
        if (dispatcher is null)
        {
            _pending.Enqueue(action);
            return;
        }

        if (dispatcher.HasShutdownStarted)
        {
            return;
        }

        _ = dispatcher.BeginInvoke(() =>
        {
            while (_pending.TryDequeue(out var pending))
            {
                RunSafely(pending);
            }

            RunSafely(action);
        });
    }

    private void RunSafely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var dispatcher = _dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
        {
            dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
        }

        if (_thread is not null && _thread.IsAlive)
        {
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Поток панелей мониторов не завершился после остановки.");
            }
        }

        _ready.Dispose();
    }
}

internal sealed class TrayMonitorPanelWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private const int SwpNoActivate = 0x0010;
    private const int SwpNoSize = 0x0001;
    private const int SwpFrameChanged = 0x0020;
    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly ConcurrentDictionary<IconLoadKey, Task<ImageSource?>> IconLoads = new();
    private readonly Func<string, bool> _activateIcon;
    private readonly Func<string, bool> _showContextMenu;
    private readonly TrayIconSettingsService _iconSettings;
    private readonly Action<string> _openUi;
    private readonly Action<TrayAppRecoveryRecord> _restoreTrayAppWindow;
    private readonly Func<bool> _toggleOverflow;
    private readonly Action<Exception> _onError;
    private readonly StackPanel _icons = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _clock = new()
    {
        FontSize = 13,
        FontWeight = FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12, 0, 12, 0)
    };
    private DisplayDto _display;
    private string _iconSignature = string.Empty;
    private string _hiddenWindowSignature = string.Empty;
    private TrayLiveIconDto[] _trayIcons = [];
    private TrayAppRecoveryRecord[] _pendingTrayApps = [];
    private bool _isEnglish;
    private string _clockFormat;
    private TrayPanelTheme _theme = TrayPanelPresentation.CreateTheme("dark", TrayXSettings.DefaultAccent, "dark");
    private Border? _surface;

    public TrayMonitorPanelWindow(
        DisplayDto display,
        Func<string, bool> activateIcon,
        Func<string, bool> showContextMenu,
        TrayIconSettingsService iconSettings,
        Action<string> openUi,
        Action<TrayAppRecoveryRecord> restoreTrayAppWindow,
        Func<bool> toggleOverflow,
        Action<Exception> onError,
        TrayXSettings settings)
    {
        _display = display;
        _activateIcon = activateIcon;
        _showContextMenu = showContextMenu;
        _iconSettings = iconSettings;
        _openUi = openUi;
        _restoreTrayAppWindow = restoreTrayAppWindow;
        _toggleOverflow = toggleOverflow;
        _onError = onError;
        _isEnglish = string.Equals(settings.Language, "en", StringComparison.OrdinalIgnoreCase);
        _clockFormat = settings.ClockFormat;
        UpdateAppearance(settings.ThemeMode, settings.Accent);
        Title = "TrayX";
        Width = Math.Max(1, Math.Min(1100, display.WorkWidth * 96.0 / Math.Max(96, display.DpiX)));
        Height = 52;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        Content = CreatePanel();
        SourceInitialized += (_, _) =>
        {
            ApplyWindowStyles();
            PositionPanel();
        };
        Loaded += (_, _) =>
        {
            UpdateLayout();
            PositionPanel();
        };

        var clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clockTimer.Tick += (_, _) => UpdateClock();
        clockTimer.Start();
        var foregroundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        foregroundTimer.Tick += (_, _) => UpdateFullscreenVisibility();
        foregroundTimer.Start();
        Closed += (_, _) =>
        {
            clockTimer.Stop();
            foregroundTimer.Stop();
        };
        UpdateClock();
    }

    public void UpdateDisplay(DisplayDto display)
    {
        var dpiChanged = _display.DpiX != display.DpiX || _display.DpiY != display.DpiY;
        _display = display;
        Width = Math.Max(1, Math.Min(1100, display.WorkWidth * 96.0 / Math.Max(96, display.DpiX)));
        UpdateLayout();
        PositionPanel();
        if (dpiChanged)
        {
            RebuildIconStrip();
        }
    }

    public void UpdateLanguage(string language)
    {
        _isEnglish = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        RebuildIconStrip();
    }

    public void UpdateClockFormat(string clockFormat)
    {
        _clockFormat = clockFormat;
        UpdateClock();
    }

    public void UpdateAppearance(string? themeMode, string? accent)
    {
        _theme = TrayPanelPresentation.CreateTheme(
            themeMode,
            accent,
            new TrayX.Windows.Themes.WindowsThemeService().GetMode());
        _clock.Foreground = new SolidColorBrush(_theme.Text);

        if (_surface is not null)
        {
            _surface.Background = new SolidColorBrush(_theme.Surface);
            _surface.BorderBrush = new SolidColorBrush(_theme.Border);
        }

        RebuildIconStrip();
    }

    public void UpdateIcons(TraySnapshotDto snapshot)
    {
        var icons = TrayPanelPresentation.SelectIconsForMonitor(snapshot.Icons, _display.Index);
        var signature = string.Join('|', icons.Select(icon => $"{icon.Id}:{icon.Name}:{icon.RegistryKey}"));
        if (signature == _iconSignature)
        {
            return;
        }

        _iconSignature = signature;
        _trayIcons = icons;
        RebuildIconStrip();
    }

    public void UpdateHiddenWindows(IReadOnlyList<TrayAppRecoveryRecord> pending)
    {
        var ordered = pending
            .OrderByDescending(record => record.PreparedAtUtc)
            .ToArray();
        var signature = string.Join(
            '|',
            ordered.Select(record =>
                $"{record.WindowHandle}:{record.ProcessId}:{record.ProcessStartTimeUtcTicks}:{record.WindowTitle}"));
        if (signature == _hiddenWindowSignature)
        {
            return;
        }

        _hiddenWindowSignature = signature;
        _pendingTrayApps = ordered;
        RebuildIconStrip();
    }

    private UIElement CreatePanel()
    {
        var surface = new Border
        {
            Margin = new Thickness(6),
            Padding = new Thickness(10, 5, 10, 5),
            Background = new SolidColorBrush(_theme.Surface),
            BorderBrush = new SolidColorBrush(_theme.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 2,
                Opacity = 0.3,
                Color = Colors.Black
            }
        };
        _surface = surface;
        var layout = new DockPanel { LastChildFill = true };
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(actions, Dock.Right);

        var clockButton = CreateClockButton(_clock, _isEnglish ? "Open quick settings" : "Открыть быстрые настройки");
        clockButton.Click += (_, _) => OpenUi("panel");
        actions.Children.Add(clockButton);

        var openButton = CreateActionButton(CreateTrayXLogo(), _isEnglish ? "Open TrayX" : "Открыть TrayX");
        openButton.Click += (_, _) => OpenUi("dashboard");
        actions.Children.Add(openButton);

        var overflowButton = CreateActionButton(CreateChevron(), _isEnglish ? "Hidden Windows tray icons" : "Скрытые значки Windows");
        overflowButton.Click += (_, _) => RunAction(_toggleOverflow);
        actions.Children.Insert(0, overflowButton);

        layout.Children.Add(actions);
        var scroll = new ScrollViewer
        {
            Content = _icons,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false
        };
        layout.Children.Add(scroll);
        surface.Child = layout;
        return surface;
    }

    private UIElement CreateChevron()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2,7 L 7,2 L 12,7"),
            Stroke = new SolidColorBrush(_theme.MutedText),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 14,
            Height = 9,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return path;
    }

    private Button CreateIconButton(TrayLiveIconDto icon)
    {
        var fallback = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(icon.Name) ? "?" : icon.Name[..1].ToUpperInvariant(),
            Foreground = new SolidColorBrush(_theme.Text),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var image = new Image
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed
        };
        var content = new Grid();
        content.Children.Add(fallback);
        content.Children.Add(image);
        var button = CreateActionButton(content, icon.Name);
        button.Click += (_, _) => RunAction(() => _activateIcon(icon.Id));
        if (icon.RegistryKey is { } key)
        {
            _ = ApplyIconImageAsync(key, image, fallback);
        }

        var menu = new ContextMenu();
        var activate = new MenuItem { Header = _isEnglish ? "Open" : "Открыть" };
        activate.Click += (_, _) => RunAction(() => _activateIcon(icon.Id));
        var context = new MenuItem
        {
            Header = _isEnglish ? "Application context menu" : "Контекстное меню приложения"
        };
        context.Click += (_, _) => RunAction(() => _showContextMenu(icon.Id));
        menu.Items.Add(activate);
        menu.Items.Add(context);
        button.ContextMenu = menu;
        return button;
    }

    private Button CreateHiddenWindowButton(TrayAppRecoveryRecord record)
    {
        var title = string.IsNullOrWhiteSpace(record.WindowTitle)
            ? record.WindowClass
            : record.WindowTitle;
        var fallback = new TextBlock
        {
            Text = title.Length == 0 ? "?" : title[..1].ToUpperInvariant(),
            Foreground = new SolidColorBrush(_theme.Text),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var button = CreateActionButton(
            fallback,
            _isEnglish ? $"Restore hidden window: {title}" : $"Восстановить скрытое окно: {title}");
        button.Background = new SolidColorBrush(_theme.AccentSurface);
        button.Click += (_, _) => RunAction(() =>
        {
            _restoreTrayAppWindow(record);
            return true;
        });

        var menu = new ContextMenu();
        var restore = new MenuItem { Header = _isEnglish ? "Restore window" : "Восстановить окно" };
        restore.Click += (_, _) => RunAction(() =>
        {
            _restoreTrayAppWindow(record);
            return true;
        });
        var manage = new MenuItem { Header = _isEnglish ? "Tray apps…" : "Приложения в трей…" };
        manage.Click += (_, _) => OpenUi("tray-apps");
        menu.Items.Add(restore);
        menu.Items.Add(manage);
        button.ContextMenu = menu;
        return button;
    }

    private void RebuildIconStrip()
    {
        _icons.Children.Clear();
        var visibleIcons = _trayIcons;
        foreach (var icon in visibleIcons)
        {
            _icons.Children.Add(CreateIconButton(icon));
        }

        if (_pendingTrayApps.Length == 0)
        {
            return;
        }

        if (visibleIcons.Length > 0)
        {
            _icons.Children.Add(new Border
            {
                Width = 1,
                Height = 24,
                Margin = new Thickness(5, 0, 5, 0),
                Background = new SolidColorBrush(_theme.Border)
            });
        }

        var ordered = _pendingTrayApps
            .OrderByDescending(record => record.PreparedAtUtc)
            .Take(20)
            .ToArray();
        foreach (var record in ordered)
        {
            _icons.Children.Add(CreateHiddenWindowButton(record));
        }

        var remaining = _pendingTrayApps.Length - ordered.Length;
        if (remaining > 0)
        {
            var more = CreateActionButton(
                new TextBlock
                {
                    Text = $"+{remaining}",
                    Foreground = new SolidColorBrush(_theme.Text),
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center
                },
                _isEnglish ? "Open Tray apps to restore more windows" : "Открыть Tray Apps для восстановления остальных окон");
            more.Click += (_, _) => OpenUi("tray-apps");
            _icons.Children.Add(more);
        }
    }

    private async Task ApplyIconImageAsync(string key, Image image, TextBlock fallback)
    {
        try
        {
            var pixelWidth = TrayPanelPresentation.GetIconDecodePixelWidth(_display.DpiX);
            var cacheKey = new IconLoadKey(key, pixelWidth);
            var load = IconLoads.GetOrAdd(cacheKey, _ => Task.Run(() => LoadIcon(key, pixelWidth)));
            var source = await load.ConfigureAwait(false);
            if (!TrayPanelPresentation.CanApplyIconImage(
                    source,
                    image.Dispatcher.HasShutdownStarted,
                    image.Dispatcher.HasShutdownFinished))
            {
                return;
            }

            _ = image.Dispatcher.BeginInvoke(() =>
            {
                image.Source = source;
                image.Visibility = Visibility.Visible;
                fallback.Visibility = Visibility.Collapsed;
            });
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    private ImageSource? LoadIcon(string key, int decodePixelWidth)
    {
        var encoded = _iconSettings.GetIconImage(key);
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromBase64String(encoded);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.DecodePixelWidth = decodePixelWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception exception) when (
            exception is FormatException or
                NotSupportedException or
                IOException or
                ArgumentException)
        {
            _onError(exception);
            return null;
        }
    }

    private void RunAction(Func<bool> action)
    {
        _ = Task.Run(() =>
        {
            try
            {
                _ = action();
            }
            catch (Exception exception)
            {
                _onError(exception);
            }
        });
    }

    private void OpenUi(string page)
    {
        try
        {
            _openUi(page);
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    private readonly record struct IconLoadKey(string Key, int PixelWidth);

    private Button CreateActionButton(UIElement content, string tooltip) => new()
    {
        Content = content,
        ToolTip = tooltip,
        Width = 38,
        Height = 38,
        Margin = new Thickness(2, 0, 2, 0),
        Padding = new Thickness(0),
        Background = new SolidColorBrush(Color.FromArgb(1, _theme.Text.R, _theme.Text.G, _theme.Text.B)),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private Button CreateClockButton(TextBlock content, string tooltip) => new()
    {
        Content = content,
        ToolTip = tooltip,
        MinWidth = 84,
        Height = 38,
        Margin = new Thickness(2, 0, 2, 0),
        Padding = new Thickness(8, 0, 8, 0),
        Background = new SolidColorBrush(Color.FromArgb(1, _theme.Text.R, _theme.Text.G, _theme.Text.B)),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    private UIElement CreateTrayXLogo()
    {
        var image = new Image
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform
        };
        var iconPath = Path.Combine(AppContext.BaseDirectory, "trayx.ico");
        try
        {
            if (!File.Exists(iconPath))
            {
                throw new FileNotFoundException("TrayX icon was not found beside TrayX.Core.", iconPath);
            }

            var source = new BitmapImage();
            source.BeginInit();
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.UriSource = new Uri(iconPath, UriKind.Absolute);
            source.DecodePixelWidth = 24;
            source.EndInit();
            source.Freeze();
            image.Source = source;
        }
        catch (Exception exception) when (
            exception is IOException or
                NotSupportedException or
                ArgumentException or
                InvalidOperationException)
        {
            _onError(exception);
            return new TextBlock
            {
                Text = "T",
                Foreground = new SolidColorBrush(_theme.Text),
                FontWeight = FontWeights.Bold,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
        }

        return image;
    }

    private void UpdateClock() =>
        _clock.Text = TrayXSettings.FormatClockTime(DateTime.Now, _clockFormat);

    private void PositionPanel()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        const int margin = 8;
        var dpiX = Math.Max(96, (int)_display.DpiX);
        var dpiY = Math.Max(96, (int)_display.DpiY);
        var width = (int)Math.Round(ActualWidth * dpiX / 96.0);
        var height = (int)Math.Round(ActualHeight * dpiY / 96.0);
        var x = _display.WorkX + Math.Max(0, (_display.WorkWidth - width) / 2);
        var y = _display.WorkY + Math.Max(0, _display.WorkHeight - height - margin);
        _ = SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    private void ApplyWindowStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExToolWindow | WsExNoActivate);
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoSize | SwpNoActivate | SwpFrameChanged);

        if (HwndSource.FromHwnd(handle) is { } source)
        {
            source.AddHook(OnWindowMessage);
        }
    }

    private IntPtr OnWindowMessage(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private void UpdateFullscreenVisibility()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == GetShellWindow())
        {
            return;
        }

        _ = GetWindowThreadProcessId(foreground, out var processId);
        if (processId == Environment.ProcessId || IsIconic(foreground) ||
            !GetWindowRect(foreground, out var bounds))
        {
            return;
        }

        var coversDisplay = TrayPanelPresentation.CoversDisplay(
            _display.X,
            _display.Y,
            _display.Width,
            _display.Height,
            bounds.Left,
            bounds.Top,
            bounds.Right,
            bounds.Bottom);

        if (coversDisplay && IsVisible)
        {
            Hide();
        }
        else if (!coversDisplay && !IsVisible)
        {
            Show();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
