using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class SystemViewModel : PageViewModelBase
{
    private const int HistoryLength = 60;

    private readonly UiContext _ui;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _cpuHistory = new();
    private readonly List<double> _netInHistory = new();
    private readonly List<double> _netOutHistory = new();

    private IReadOnlyList<double> _cpuPoints = Array.Empty<double>();
    private IReadOnlyList<double> _netInPoints = Array.Empty<double>();
    private IReadOnlyList<double> _netOutPoints = Array.Empty<double>();
    private bool _refreshing;
    private bool _hasData;
    private int _ticks;

    private double _cpuPercent;
    private double _memoryPercent;
    private long _memoryUsed;
    private long _memoryTotal;
    private double _networkIn;
    private double _networkOut;
    private long _uptimeSeconds;
    private int _processCount;
    private long _driveFree;
    private long _driveTotal;
    private int? _batteryPercent;
    private bool? _batteryCharging;
    private string _coreInfo = string.Empty;

    public SystemViewModel(UiContext ui)
    {
        _ui = ui;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(forceStatus: true));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _timer.Tick += (_, _) => _ = RefreshAsync();

        _ui.SettingsChanged += (_, settings) =>
        {
            _timer.Interval = TimeSpan.FromMilliseconds(settings.MonitorIntervalMs);
        };
    }

    public override string Key => "system";

    protected override string TitleKey => "System_Title";

    protected override string SubtitleKey => "System_Subtitle";

    public ObservableCollection<ProcessItemViewModel> TopProcesses { get; } = new();

    public ObservableCollection<ModuleItemViewModel> Modules { get; } = new();

    public IRelayCommand RefreshCommand { get; }

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

    public bool HasData
    {
        get => _hasData;
        private set => SetProperty(ref _hasData, value);
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

    public string NetworkInText => UiFormat.Rate(_networkIn);

    public string NetworkOutText => UiFormat.Rate(_networkOut);

    public string UptimeText => UiFormat.Uptime(_uptimeSeconds);

    public string ProcessCountText => _processCount.ToString(Loc.Instance.Culture);

    public double DiskUsedPercent => _driveTotal > 0
        ? Math.Clamp((_driveTotal - _driveFree) * 100d / _driveTotal, 0, 100)
        : 0;

    public string DiskDetail => _driveTotal > 0
        ? string.Format(Loc.T("System_Free"), UiFormat.Bytes(_driveFree), UiFormat.Bytes(_driveTotal))
        : "—";

    public bool HasBattery => _batteryPercent.HasValue;

    public string BatteryText
    {
        get
        {
            if (!_batteryPercent.HasValue)
            {
                return Loc.T("System_NoBattery");
            }

            var state = _batteryCharging == true ? Loc.T("System_Charging") : Loc.T("System_NotCharging");
            return $"{_batteryPercent.Value}% · {state}";
        }
    }

    public string CoreInfo => _coreInfo;

    protected override async Task OnActivatedAsync()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_ui.Settings.MonitorIntervalMs);
        _timer.Start();
        await RefreshAsync(forceStatus: true).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _timer.Stop();
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MemoryDetail));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(DiskDetail));
        OnPropertyChanged(nameof(BatteryText));
        RebuildModules();
        RebuildCoreInfo();
    }

    private async Task RefreshAsync(bool forceStatus = false)
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

            _ticks++;
            if (forceStatus || _ticks % 8 == 0)
            {
                var status = await _ui.GetStatusAsync(silent: true).ConfigureAwait(true);
                if (status is not null)
                {
                    ApplyStatus(status);
                }
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyStats(SystemStatsDto stats)
    {
        HasData = true;
        CpuPercent = Math.Clamp(stats.CpuPercent, 0, 100);
        MemoryPercent = Math.Clamp(stats.MemoryPercent, 0, 100);
        _memoryUsed = stats.MemoryUsedBytes;
        _memoryTotal = stats.MemoryTotalBytes;
        _networkIn = stats.NetworkInBytesPerSecond;
        _networkOut = stats.NetworkOutBytesPerSecond;
        _uptimeSeconds = stats.UptimeSeconds;
        _processCount = stats.ProcessCount;
        _driveFree = stats.SystemDriveFreeBytes;
        _driveTotal = stats.SystemDriveTotalBytes;
        _batteryPercent = stats.BatteryPercent;
        _batteryCharging = stats.BatteryCharging;

        Push(_cpuHistory, CpuPercent);
        Push(_netInHistory, _networkIn);
        Push(_netOutHistory, _networkOut);
        CpuPoints = _cpuHistory.ToArray();
        NetInPoints = _netInHistory.ToArray();
        NetOutPoints = _netOutHistory.ToArray();

        TopProcesses.Clear();
        foreach (var process in stats.TopProcesses.Take(6))
        {
            TopProcesses.Add(new ProcessItemViewModel(process, stats.MemoryUsedBytes));
        }

        OnPropertyChanged(nameof(MemoryDetail));
        OnPropertyChanged(nameof(NetworkInText));
        OnPropertyChanged(nameof(NetworkOutText));
        OnPropertyChanged(nameof(UptimeText));
        OnPropertyChanged(nameof(ProcessCountText));
        OnPropertyChanged(nameof(DiskUsedPercent));
        OnPropertyChanged(nameof(DiskDetail));
        OnPropertyChanged(nameof(HasBattery));
        OnPropertyChanged(nameof(BatteryText));
    }

    private void ApplyStatus(CoreStatusDto status)
    {
        _status = status;
        RebuildModules();
        RebuildCoreInfo();
    }

    private CoreStatusDto? _status;

    private void RebuildModules()
    {
        if (_status is null)
        {
            return;
        }

        Modules.Clear();
        foreach (var module in _status.Modules)
        {
            Modules.Add(new ModuleItemViewModel(module));
        }
    }

    private void RebuildCoreInfo()
    {
        if (_status is null)
        {
            return;
        }

        _coreInfo = string.Format(
            Loc.T("System_CoreInfo"),
            _status.Version,
            _status.WindowsBuild,
            _status.ProtocolVersion);
        OnPropertyChanged(nameof(CoreInfo));
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
