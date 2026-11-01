using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class DashboardViewModel : PageViewModelBase
{
    private const int HistoryLength = 40;

    private readonly UiContext _ui;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _netInHistory = new();
    private readonly List<double> _netOutHistory = new();

    private CancellationTokenSource? _volumeDebounce;
    private IReadOnlyList<double> _cpuPoints = Array.Empty<double>();
    private IReadOnlyList<double> _netInPoints = Array.Empty<double>();
    private IReadOnlyList<double> _netOutPoints = Array.Empty<double>();
    private bool _refreshing;
    private bool _hasStats;
    private int _ticks;
    private DateTime _lastVolumeChangeUtc = DateTime.MinValue;

    private double _cpuPercent;
    private double _memoryPercent;
    private double _diskPercent;
    private long _memoryUsed;
    private long _memoryTotal;
    private long _uptimeSeconds;
    private int _processCount;
    private double _networkIn;
    private double _networkOut;

    private double _masterVolume;
    private bool _isMuted;
    private string _deviceName = "—";
    private int _sessionsCount;

    private int _displayCount;
    private int _trayCount;
    private int _trayPromoted;
    private int _modulesRunning;
    private int _modulesTotal;
    private bool _isWindowsDark = true;
    private string _coreVersion = "—";

    public DashboardViewModel(UiContext ui)
    {
        _ui = ui;
        NavigateCommand = new RelayCommand<string>(Navigate);
        MuteCommand = new RelayCommand(() => _ = ToggleMuteAsync());
        SetWindowsThemeCommand = new RelayCommand<string>(mode => _ = SetWindowsThemeAsync(mode));

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_ui.Settings.MonitorIntervalMs)
        };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _ui.SettingsChanged += (_, settings) =>
        {
            _timer.Interval = TimeSpan.FromMilliseconds(settings.MonitorIntervalMs);
        };
        _ui.WindowsThemeChanged += (_, theme) =>
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
        };
    }

    public override string Key => "dashboard";

    protected override string TitleKey => "Dash_Title";

    protected override string SubtitleKey => "Dash_Subtitle";

    public event EventHandler<string>? NavigationRequested;

    public ObservableCollection<ModuleItemViewModel> Modules { get; } = new();

    public IRelayCommand<string> NavigateCommand { get; }

    public IRelayCommand MuteCommand { get; }

    public IRelayCommand<string> SetWindowsThemeCommand { get; }

    public IReadOnlyList<double> CpuPoints
    {
        get => _cpuPoints;
        private set => SetProperty(ref _cpuPoints, value);
    }

    public IReadOnlyList<double> NetInPoints
    {
        get => _netInPoints;
        private set => SetProperty(ref _netInPoints, value);
    }

    public IReadOnlyList<double> NetOutPoints
    {
        get => _netOutPoints;
        private set => SetProperty(ref _netOutPoints, value);
    }

    public bool HasStats
    {
        get => _hasStats;
        private set => SetProperty(ref _hasStats, value);
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set
        {
            if (SetProperty(ref _cpuPercent, value))
            {
                OnPropertyChanged(nameof(CpuText));
            }
        }
    }

    public string CpuText => $"{CpuPercent:0}%";

    public double MemoryPercent
    {
        get => _memoryPercent;
        private set
        {
            if (SetProperty(ref _memoryPercent, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public string MemoryText => $"{MemoryPercent:0}%";

    public string MemoryDetail => _memoryTotal > 0
        ? string.Format(Loc.T("System_MemoryUsed"), UiFormat.Bytes(_memoryUsed), UiFormat.Bytes(_memoryTotal))
        : "—";

    public double DiskPercent
    {
        get => _diskPercent;
        private set => SetProperty(ref _diskPercent, value);
    }

    public string DiskText => $"{DiskPercent:0}%";

    public string ProcessCountText => _processCount.ToString(Loc.Instance.Culture);

    public string UptimeText => UiFormat.Uptime(_uptimeSeconds);

    public string NetworkInText => UiFormat.Rate(_networkIn);

    public string NetworkOutText => UiFormat.Rate(_networkOut);

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (!SetProperty(ref _masterVolume, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(MasterVolumeText));
            _lastVolumeChangeUtc = DateTime.UtcNow;
            QueueVolume(clamped / 100);
        }
    }

    public string MasterVolumeText => $"{MasterVolume:0}%";

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteTooltip));
            }
        }
    }

    public string MuteTooltip => IsMuted ? Loc.T("Header_Unmute") : Loc.T("Header_Mute");

    public string DeviceName
    {
        get => _deviceName;
        private set => SetProperty(ref _deviceName, value);
    }

    public string SessionsText => string.Format(Loc.T("Dash_AudioSessions"), _sessionsCount);

    public string DisplaysHint => string.Format(Loc.T("Dash_DisplaysHint"), _displayCount);

    public string TrayHint => string.Format(Loc.T("Dash_TrayHint"), _trayCount, _trayPromoted);

    public string ModulesOkText => string.Format(Loc.T("Dash_ModulesOk"), _modulesRunning, _modulesTotal);

    public bool IsWindowsDark
    {
        get => _isWindowsDark;
        private set
        {
            if (SetProperty(ref _isWindowsDark, value))
            {
                OnPropertyChanged(nameof(WindowsThemeText));
            }
        }
    }

    public string WindowsThemeText => IsWindowsDark
        ? Loc.T("Quick_Dark")
        : Loc.T("Quick_Light");

    public string CoreVersion => _coreVersion;

    protected override async Task OnActivatedAsync()
    {
        _timer.Start();
        await LoadAllAsync().ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _timer.Stop();
        _volumeDebounce?.Cancel();
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(SessionsText));
        OnPropertyChanged(nameof(DisplaysHint));
        OnPropertyChanged(nameof(TrayHint));
        OnPropertyChanged(nameof(ModulesOkText));
        OnPropertyChanged(nameof(WindowsThemeText));
        OnPropertyChanged(nameof(MemoryDetail));
        OnPropertyChanged(nameof(UptimeText));
    }

    private void Navigate(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            NavigationRequested?.Invoke(this, page);
        }
    }

    private async Task LoadAllAsync()
    {
        _ticks = 0;
        await RefreshAsync().ConfigureAwait(true);
        await RefreshPeripheralAsync().ConfigureAwait(true);
        await RefreshStatusAsync().ConfigureAwait(true);

        var theme = await _ui.GetWindowsThemeAsync(silent: true).ConfigureAwait(true);
        if (theme is not null)
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var stats = await _ui.GetStatsAsync(silent: true).ConfigureAwait(true);
            if (stats is not null)
            {
                ApplyStats(stats);
            }

            var audio = await _ui.GetAudioStateAsync(silent: true).ConfigureAwait(true);
            if (audio is not null)
            {
                ApplyAudio(audio);
            }

            _ticks++;
            if (_ticks % 4 == 0)
            {
                await RefreshPeripheralAsync().ConfigureAwait(true);
            }

            if (_ticks % 5 == 0)
            {
                await RefreshStatusAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshPeripheralAsync()
    {
        var displays = await _ui.GetDisplaysAsync(silent: true).ConfigureAwait(true);
        if (displays is not null)
        {
            _displayCount = displays.Count;
            OnPropertyChanged(nameof(DisplaysHint));
        }

        var tray = await _ui.GetTrayIconsAsync(silent: true).ConfigureAwait(true);
        if (tray is not null)
        {
            _trayCount = tray.Count;
            _trayPromoted = tray.Count(icon => icon.IsPromoted);
            OnPropertyChanged(nameof(TrayHint));
        }
    }

    private async Task RefreshStatusAsync()
    {
        var status = await _ui.GetStatusAsync(silent: true).ConfigureAwait(true);
        if (status is null)
        {
            return;
        }

        _coreVersion = status.Version;
        _modulesTotal = status.Modules.Count;
        _modulesRunning = status.Modules.Count(module => module.IsRunning);
        Modules.Clear();
        foreach (var module in status.Modules)
        {
            Modules.Add(new ModuleItemViewModel(module));
        }

        OnPropertyChanged(nameof(ModulesOkText));
        OnPropertyChanged(nameof(CoreVersion));
    }

    private void ApplyStats(SystemStatsDto stats)
    {
        HasStats = true;
        CpuPercent = Math.Clamp(stats.CpuPercent, 0, 100);
        MemoryPercent = Math.Clamp(stats.MemoryPercent, 0, 100);
        _memoryUsed = stats.MemoryUsedBytes;
        _memoryTotal = stats.MemoryTotalBytes;
        _uptimeSeconds = stats.UptimeSeconds;
        _processCount = stats.ProcessCount;
        _networkIn = stats.NetworkInBytesPerSecond;
        _networkOut = stats.NetworkOutBytesPerSecond;
        DiskPercent = stats.SystemDriveTotalBytes > 0
            ? Math.Clamp(
                (stats.SystemDriveTotalBytes - stats.SystemDriveFreeBytes) * 100d /
                stats.SystemDriveTotalBytes,
                0,
                100)
            : 0;

        Push(_cpuHistory, CpuPercent);
        Push(_netInHistory, _networkIn);
        Push(_netOutHistory, _networkOut);

        CpuPoints = _cpuHistory.ToArray();
        NetInPoints = _netInHistory.ToArray();
        NetOutPoints = _netOutHistory.ToArray();

        OnPropertyChanged(nameof(MemoryDetail));
        OnPropertyChanged(nameof(DiskText));
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(NetworkInText));
        OnPropertyChanged(nameof(NetworkOutText));
    }

    private void ApplyAudio(AudioStateDto state)
    {
        DeviceName = state.HasDevice ? state.DeviceName : Loc.T("Audio_Disabled");
        _sessionsCount = state.Sessions.Count;
        IsMuted = state.Muted;

        if (DateTime.UtcNow - _lastVolumeChangeUtc > TimeSpan.FromSeconds(1))
        {
            var percent = Math.Round(state.Volume * 100);
            if (Math.Abs(percent - _masterVolume) >= 1)
            {
                _masterVolume = percent;
                OnPropertyChanged(nameof(MasterVolume));
                OnPropertyChanged(nameof(MasterVolumeText));
            }
        }

        OnPropertyChanged(nameof(SessionsText));
    }

    private async Task ToggleMuteAsync()
    {
        var target = !IsMuted;
        var level = await _ui.SetMasterMuteAsync(target).ConfigureAwait(true);
        if (level is not null)
        {
            IsMuted = level.Muted;
            _lastVolumeChangeUtc = DateTime.UtcNow;
        }
    }

    private async void QueueVolume(double volume)
    {
        _volumeDebounce?.Cancel();
        _volumeDebounce?.Dispose();
        var source = new CancellationTokenSource();
        _volumeDebounce = source;

        try
        {
            await Task.Delay(130, source.Token).ConfigureAwait(true);
            var level = await _ui.SetMasterVolumeAsync(volume).ConfigureAwait(true);
            if (level is not null)
            {
                IsMuted = level.Muted;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_volumeDebounce, source))
            {
                _volumeDebounce = null;
            }

            source.Dispose();
        }
    }

    private async Task SetWindowsThemeAsync(string? mode)
    {
        if (mode is not ("dark" or "light" or "system"))
        {
            return;
        }

        var theme = await _ui.SetWindowsThemeAsync(mode).ConfigureAwait(true);
        if (theme is not null)
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
            _ui.Report(Loc.T("Status_WindowsThemeChanged"));
        }
    }

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength)
        {
            history.RemoveAt(0);
        }
    }
}
