using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class QuickPanelViewModel : ObservableObject, ITrayIconActions
{
    private const int MaxPanelIcons = 24;

    private readonly UiContext _ui;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _clock;

    private CancellationTokenSource? _volumeDebounce;
    private CancellationTokenSource? _brightnessDebounce;
    private DateTime _calendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _selectedDate = DateTime.Today;
    private DateTime _today = DateTime.Today;
    private int _barCount;
    private int _hiddenCount;
    private bool _active;
    private bool _loading;
    private bool _connected;
    private bool _isMuted;
    private bool _isWindowsDark = true;
    private bool _hasBrightness;
    private double _masterVolume;
    private double _brightness;
    private int _displayIndex;
    private string _displayStableId = string.Empty;
    private DateTime _lastVolumeChangeUtc = DateTime.MinValue;
    private DateTime _lastBrightnessChangeUtc = DateTime.MinValue;

    public QuickPanelViewModel(UiContext ui)
    {
        _ui = ui;

        ToggleMuteCommand = new RelayCommand(() => _ = ToggleMuteAsync());
        NavigateCommand = new RelayCommand<string>(Navigate);
        OpenSettingsCommand = new RelayCommand<string>(page => _ = OpenSettingsAsync(page));
        CloseCommand = new RelayCommand(() => CloseRequested?.Invoke(this, EventArgs.Empty));
        SetWindowsThemeCommand = new RelayCommand<string>(mode => _ = SetWindowsThemeAsync(mode));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());
        PreviousMonthCommand = new RelayCommand(() => ShiftMonth(-1));
        NextMonthCommand = new RelayCommand(() => ShiftMonth(1));
        ThisMonthCommand = new RelayCommand(SelectToday);
        SelectDateCommand = new RelayCommand<CalendarDay>(SelectDate);
        OpenDateTimeSettingsCommand = new RelayCommand(() => _ = OpenSettingsAsync("date-time"));
        ToggleOverflowCommand = new RelayCommand(() => _ = ToggleOverflowAsync());

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _timer.Tick += (_, _) => _ = RefreshAsync(silent: true);

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => RefreshClock();

        _ui.WindowsThemeChanged += (_, theme) =>
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
        };
        _ui.ConnectionChanged += (_, _) => IsConnected = _ui.IsConnected;
        _ui.SettingsChanged += (_, _) => OnPropertyChanged(nameof(ClockText));
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(WeekdayNames));
            OnPropertyChanged(nameof(CalendarTitle));
            OnPropertyChanged(nameof(CalendarWeeks));
            OnPropertyChanged(nameof(SelectedDateText));
            OnPropertyChanged(nameof(ClockText));
            OnPropertyChanged(nameof(DateText));
        };
        _ui.AudioChanged += (_, changed) =>
        {
            if (DateTime.UtcNow - _lastVolumeChangeUtc > TimeSpan.FromSeconds(1))
            {
                ApplyVolume(changed.Volume);
                IsMuted = changed.Muted;
            }
        };
        _ui.TrayLiveChanged += (_, snapshot) =>
        {
            if (_active)
            {
                ApplyTrayIcons(snapshot);
            }
        };
    }

    public event EventHandler<string>? NavigationRequested;

    public event EventHandler? CloseRequested;

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand<string> NavigateCommand { get; }

    public IRelayCommand<string> OpenSettingsCommand { get; }

    public IRelayCommand CloseCommand { get; }

    public IRelayCommand<string> SetWindowsThemeCommand { get; }

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand PreviousMonthCommand { get; }

    public IRelayCommand NextMonthCommand { get; }

    public IRelayCommand ThisMonthCommand { get; }

    public IRelayCommand<CalendarDay> SelectDateCommand { get; }

    public IRelayCommand OpenDateTimeSettingsCommand { get; }

    public IRelayCommand ToggleOverflowCommand { get; }

    public ObservableCollection<TrayLiveIconItemViewModel> TrayIcons { get; } = new();

    public string[] WeekdayNames => MonthCalendar.WeekdayNames(Loc.Instance.Culture);

    public IReadOnlyList<IReadOnlyList<CalendarDay>> CalendarWeeks =>
        MonthCalendar.Build(_calendarMonth, DateTime.Today, Loc.Instance.Culture, _selectedDate);

    public string CalendarTitle => _calendarMonth.ToString("MMMM yyyy", Loc.Instance.Culture);

    public string SelectedDateText => _selectedDate.ToString("D", Loc.Instance.Culture);

    public string ClockText => TrayXSettings.FormatClockTime(
        DateTime.Now,
        _ui.Settings.ClockFormat,
        Loc.Instance.Culture);

    public string DateText => DateTime.Now.ToString("dddd, d MMMM", Loc.Instance.Culture);

    public string TrayCountText => string.Format(Loc.T("Quick_TrayCount"), _barCount, _hiddenCount);

    public bool ShowTrayIcons => TrayIcons.Count > 0;

    public bool IsConnected
    {
        get => _connected;
        set
        {
            if (SetProperty(ref _connected, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => IsConnected ? Loc.T("Shell_Connected") : Loc.T("Quick_Offline");

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

    public string MuteTooltip => IsMuted ? Loc.T("Quick_Unmute") : Loc.T("Quick_Mute");

    public double Brightness
    {
        get => _brightness;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (!SetProperty(ref _brightness, clamped))
            {
                return;
            }

            OnPropertyChanged(nameof(BrightnessText));
            _lastBrightnessChangeUtc = DateTime.UtcNow;
            QueueBrightness((int)clamped);
        }
    }

    public string BrightnessText => HasBrightness ? $"{Brightness:0}%" : "—";

    public bool HasBrightness
    {
        get => _hasBrightness;
        private set
        {
            if (SetProperty(ref _hasBrightness, value))
            {
                OnPropertyChanged(nameof(BrightnessText));
            }
        }
    }

    public bool IsWindowsDark
    {
        get => _isWindowsDark;
        private set
        {
            if (SetProperty(ref _isWindowsDark, value))
            {
                OnPropertyChanged(nameof(IsWindowsLight));
            }
        }
    }

    public bool IsWindowsLight => !IsWindowsDark;

    public async Task OpenAsync()
    {
        _active = true;
        RefreshClock();
        await RefreshAsync().ConfigureAwait(true);
        _ = LoadTrayIconsAsync();
        _timer.Start();
        _clock.Start();
    }

    public void Deactivate()
    {
        _active = false;
        _timer.Stop();
        _clock.Stop();
        _volumeDebounce?.Cancel();
        _brightnessDebounce?.Cancel();
    }

    private void RefreshClock()
    {
        OnPropertyChanged(nameof(ClockText));
        OnPropertyChanged(nameof(DateText));

        var today = DateTime.Today;
        if (_today == today)
        {
            return;
        }

        var pinned = _calendarMonth.Year == _today.Year && _calendarMonth.Month == _today.Month;
        _today = today;
        if (pinned)
        {
            SetMonth(today);
        }
        else
        {
            OnPropertyChanged(nameof(CalendarWeeks));
        }
    }

    private void ShiftMonth(int delta) => SetMonth(_calendarMonth.AddMonths(delta));

    private void SelectToday()
    {
        _selectedDate = DateTime.Today;
        SetMonth(_selectedDate);
        OnPropertyChanged(nameof(SelectedDateText));
    }

    private void SelectDate(CalendarDay? day)
    {
        if (day is null)
        {
            return;
        }

        _selectedDate = day.Date;
        if (_calendarMonth.Month != day.Date.Month || _calendarMonth.Year != day.Date.Year)
        {
            SetMonth(day.Date);
        }
        else
        {
            OnPropertyChanged(nameof(CalendarWeeks));
        }

        OnPropertyChanged(nameof(SelectedDateText));
    }

    private void SetMonth(DateTime date)
    {
        _calendarMonth = new DateTime(date.Year, date.Month, 1);
        OnPropertyChanged(nameof(CalendarTitle));
        OnPropertyChanged(nameof(CalendarWeeks));
    }

    private async Task LoadTrayIconsAsync()
    {
        var snapshot = await _ui.GetTraySnapshotAsync(silent: true).ConfigureAwait(true);
        if (snapshot is not null)
        {
            ApplyTrayIcons(snapshot);
        }
    }

    private void ApplyTrayIcons(TraySnapshotDto snapshot)
    {
        TrayIcons.Clear();
        foreach (var icon in snapshot.Icons
                     .Where(icon => icon.OnBar || icon.IsRunning)
                     .OrderByDescending(icon => icon.OnBar)
                     .Take(MaxPanelIcons))
        {
            TrayIcons.Add(new TrayLiveIconItemViewModel(icon, this));
        }

        _barCount = snapshot.Icons.Count(icon => icon.OnBar);
        _hiddenCount = snapshot.Icons.Count(icon => !icon.OnBar && icon.IsRunning);

        OnPropertyChanged(nameof(ShowTrayIcons));
        OnPropertyChanged(nameof(TrayCountText));
        _ = TrayIconImageLoader.LoadAsync(_ui, TrayIcons);
    }

    private async Task ToggleOverflowAsync()
    {
        var toggled = await _ui.ToggleTrayOverflowAsync().ConfigureAwait(true);
        if (!toggled)
        {
            _ui.Report(Loc.T("Tray_OverflowFailed"));
        }

        await LoadTrayIconsAsync().ConfigureAwait(true);
    }

    public async Task ActivateAsync(TrayLiveIconItemViewModel item)
    {
        var activated = await _ui.ActivateTrayIconAsync(item.Id).ConfigureAwait(true);
        _ui.Report(activated ? Loc.T("Tray_Activated") : Loc.T("Tray_ActionFailed"));
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task ShowContextMenuAsync(TrayLiveIconItemViewModel item)
    {
        var shown = await _ui.ShowTrayIconContextMenuAsync(item.Id).ConfigureAwait(true);
        if (!shown)
        {
            _ui.Report(Loc.T("Tray_ActionFailed"));
        }
    }

    public async Task SetPromotedAsync(TrayLiveIconItemViewModel item, bool promoted)
    {
        if (item.RegistryKey is not { } key)
        {
            return;
        }

        var result = await _ui.SetTrayPromotedAsync(key, promoted).ConfigureAwait(true);
        if (result is null)
        {
            item.SetPromotedSilently(!promoted);
            _ui.Report(Loc.T("Tray_ToggleFailed"));
            return;
        }

        item.SetPromotedSilently(result.IsPromoted);
        await LoadTrayIconsAsync().ConfigureAwait(true);
    }

    private void Navigate(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            NavigationRequested?.Invoke(this, page);
        }
    }

    private async Task OpenSettingsAsync(string? page)
    {
        if (!string.IsNullOrWhiteSpace(page))
        {
            await _ui.OpenWindowsSettingsAsync(page).ConfigureAwait(true);
        }
    }

    private async Task RefreshAsync(bool silent = false)
    {
        if (_loading)
        {
            return;
        }

        _loading = true;
        try
        {
            IsConnected = _ui.IsConnected;

            var audio = await _ui.GetAudioStateAsync(silent).ConfigureAwait(true);
            if (audio is not null)
            {
                if (DateTime.UtcNow - _lastVolumeChangeUtc > TimeSpan.FromSeconds(1))
                {
                    ApplyVolume(audio.Volume);
                }

                IsMuted = audio.Muted;
            }

            var displays = await _ui.GetDisplaysAsync(silent: true).ConfigureAwait(true);
            if (displays is not null)
            {
                var primary = displays.FirstOrDefault(display => display.IsPrimary) ?? displays.FirstOrDefault();
                if (primary is not null)
                {
                    _displayIndex = primary.Index;
                    _displayStableId = primary.StableId;
                    HasBrightness = primary.Brightness.HasValue;
                    if (primary.Brightness.HasValue &&
                        DateTime.UtcNow - _lastBrightnessChangeUtc > TimeSpan.FromSeconds(1))
                    {
                        _brightness = primary.Brightness.Value;
                        OnPropertyChanged(nameof(Brightness));
                        OnPropertyChanged(nameof(BrightnessText));
                    }
                }
                else
                {
                    HasBrightness = false;
                }
            }

            var theme = await _ui.GetWindowsThemeAsync(silent: true).ConfigureAwait(true);
            if (theme is not null)
            {
                IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task ToggleMuteAsync()
    {
        var level = await _ui.SetMasterMuteAsync(!IsMuted).ConfigureAwait(true);
        if (level is not null)
        {
            IsMuted = level.Muted;
            ApplyVolume(level.Volume);
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

    private async void QueueBrightness(int percent)
    {
        var stableId = _displayStableId;
        _brightnessDebounce?.Cancel();
        _brightnessDebounce?.Dispose();
        var source = new CancellationTokenSource();
        _brightnessDebounce = source;

        try
        {
            await Task.Delay(140, source.Token).ConfigureAwait(true);
            var displays = await _ui.SetBrightnessAsync(_displayIndex, percent, stableId).ConfigureAwait(true);
            if (displays is not null)
            {
                var primary = displays.FirstOrDefault(display => display.Index == _displayIndex);
                if (primary?.Brightness is { } brightness)
                {
                    HasBrightness = true;
                    if (DateTime.UtcNow - _lastBrightnessChangeUtc > TimeSpan.FromSeconds(1))
                    {
                        _brightness = brightness;
                        OnPropertyChanged(nameof(Brightness));
                        OnPropertyChanged(nameof(BrightnessText));
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_brightnessDebounce, source))
            {
                _brightnessDebounce = null;
            }

            source.Dispose();
        }
    }

    private async Task SetWindowsThemeAsync(string? mode)
    {
        if (mode is not ("dark" or "light"))
        {
            return;
        }

        var theme = await _ui.SetWindowsThemeAsync(mode).ConfigureAwait(true);
        if (theme is not null)
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
        }
    }

    private void ApplyVolume(double volume)
    {
        var percent = Math.Round(volume * 100);
        if (Math.Abs(percent - _masterVolume) < 1)
        {
            return;
        }

        _masterVolume = percent;
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
    }
}
