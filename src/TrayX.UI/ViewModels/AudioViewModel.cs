using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class AudioViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly DispatcherTimer _peakTimer;
    private readonly Dictionary<string, CancellationTokenSource> _sessionDebounce = new();
    private readonly Dictionary<string, CancellationTokenSource> _deviceDebounce = new();

    private CancellationTokenSource? _masterDebounce;
    private bool _refreshing;
    private bool _refreshRequested;
    private bool _peakRequestInProgress;
    private bool _hasDevice;
    private bool _isMuted;
    private bool _isLoading;
    private string _deviceName = string.Empty;
    private string _error = string.Empty;
    private double _masterVolume;
    private DateTime _lastMasterChangeUtc = DateTime.MinValue;
    private double? _inputPeakLevel;
    private bool _isCompact;

    public AudioViewModel(UiContext ui)
    {
        _ui = ui;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(showErrors: true));
        ToggleMuteCommand = new RelayCommand(ToggleMute);
        OpenSoundSettingsCommand = new RelayCommand(() => _ = _ui.OpenWindowsSettingsAsync("sound"));
        OpenClassicSoundCommand = new RelayCommand(() => _ = _ui.OpenClassicSoundControlPanelAsync());
        ToggleCompactCommand = new RelayCommand(() => IsCompact = !IsCompact);
        _peakTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _peakTimer.Tick += (_, _) => _ = RefreshInputPeakAsync();

        _ui.DeviceChanged += OnDeviceChanged;
        _ui.AudioDevicesChanged += OnAudioDevicesChanged;
        _ui.AudioSessionsChanged += OnAudioSessionsChanged;
        _ui.AudioChanged += OnAudioChanged;
        _ui.SettingsChanged += OnSettingsChanged;
    }

    public override string Key => "audio";

    protected override string TitleKey => "Audio_Title";

    protected override string SubtitleKey => "Audio_Subtitle";

    public ObservableCollection<AudioSessionItemViewModel> Sessions { get; } = new();

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand OpenSoundSettingsCommand { get; }

    public IRelayCommand OpenClassicSoundCommand { get; }

    public IRelayCommand ToggleCompactCommand { get; }

    public ObservableCollection<AudioDeviceItemViewModel> OutputDevices { get; } = new();

    public ObservableCollection<AudioDeviceItemViewModel> InputDevices { get; } = new();

    public bool HasDevice
    {
        get => _hasDevice;
        private set => SetProperty(ref _hasDevice, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

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

    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool IsCompact
    {
        get => _isCompact;
        private set
        {
            if (SetProperty(ref _isCompact, value))
            {
                OnPropertyChanged(nameof(DetailsVisibility));
                OnPropertyChanged(nameof(CompactButtonText));
            }
        }
    }

    public System.Windows.Visibility DetailsVisibility =>
        IsCompact ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public string CompactButtonText => IsCompact ? Loc.T("Audio_Expand") : Loc.T("Audio_Compact");

    public bool IsHighVolumeWarning => MasterVolume >= 85;

    public string HighVolumeWarningText => Loc.T("Audio_HighVolumeWarning");

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
            OnPropertyChanged(nameof(IsHighVolumeWarning));
            _lastMasterChangeUtc = DateTime.UtcNow;
            QueueMasterVolume(clamped / 100);
        }
    }

    public string MasterVolumeText => $"{MasterVolume:0}%";

    public string SessionsText => string.Format(Loc.T("Audio_ManagedCount"), Sessions.Count);

    public double? InputPeakLevel
    {
        get => _inputPeakLevel;
        private set
        {
            if (SetProperty(ref _inputPeakLevel, value))
            {
                OnPropertyChanged(nameof(InputPeakPercent));
                OnPropertyChanged(nameof(HasInputPeak));
            }
        }
    }

    public string InputPeakPercent =>
        InputPeakLevel is { } level ? $"{Math.Round(level * 100):0}%" : string.Empty;

    public bool HasInputPeak => InputPeakLevel.HasValue;

    public bool HasSessions => Sessions.Count > 0;

    public bool ShowSessionsEmpty => !HasSessions && !IsLoading;

    protected override async Task OnActivatedAsync()
    {
        _peakTimer.Start();
        await RefreshAsync(showErrors: false).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _peakTimer.Stop();
        _masterDebounce?.Cancel();
        foreach (var source in _sessionDebounce.Values)
        {
            source.Cancel();
        }

        _sessionDebounce.Clear();
        foreach (var source in _deviceDebounce.Values)
        {
            source.Cancel();
        }

        _deviceDebounce.Clear();
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(MuteTooltip));
        OnPropertyChanged(nameof(SessionsText));
        OnPropertyChanged(nameof(CompactButtonText));
        OnPropertyChanged(nameof(HighVolumeWarningText));
        if (!HasDevice)
        {
            DeviceName = Loc.T("Audio_Disabled");
        }
    }

    internal async void QueueSessionVolume(AudioSessionItemViewModel item, double volume)
    {
        if (_sessionDebounce.TryGetValue(item.Key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var source = new CancellationTokenSource();
        _sessionDebounce[item.Key] = source;

        try
        {
            await Task.Delay(130, source.Token).ConfigureAwait(true);
            var level = await _ui.SetSessionVolumeAsync(item.Key, volume).ConfigureAwait(true);
            if (level is null)
            {
                await RefreshAsync(silent: true).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_sessionDebounce.TryGetValue(item.Key, out var current) && ReferenceEquals(current, source))
            {
                _sessionDebounce.Remove(item.Key);
            }

            source.Dispose();
        }
    }

    internal async void ToggleSessionMute(AudioSessionItemViewModel item)
    {
        var target = !item.Muted;
        item.SetMuted(target);

        var level = await _ui.SetSessionMuteAsync(item.Key, target).ConfigureAwait(true);
        if (level is null)
        {
            item.SetMuted(!target);
        }
    }

    internal async void QueueDeviceVolume(AudioDeviceItemViewModel item, double volume)
    {
        if (_deviceDebounce.TryGetValue(item.Id, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var source = new CancellationTokenSource();
        _deviceDebounce[item.Id] = source;

        try
        {
            await Task.Delay(130, source.Token).ConfigureAwait(true);
            var level = await _ui.SetAudioDeviceVolumeAsync(item.Id, volume).ConfigureAwait(true);
            if (level is not null)
            {
                item.Apply(new AudioDeviceDto(
                    item.Id,
                    item.Name,
                    item.Flow,
                    item.IsDefault,
                    level.Muted,
                    level.Volume,
                    true),
                    IsFavoriteAudioDevice(item.Id));
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_deviceDebounce.TryGetValue(item.Id, out var current) && ReferenceEquals(current, source))
            {
                _deviceDebounce.Remove(item.Id);
            }

            source.Dispose();
        }
    }

    internal Task<AudioLevelDto?> SetDeviceMuteAsync(AudioDeviceItemViewModel item, bool muted) =>
        _ui.SetAudioDeviceMuteAsync(item.Id, muted);

    internal async void SetDefaultDevice(AudioDeviceItemViewModel item)
    {
        var changed = await _ui.SetDefaultAudioDeviceAsync(item.Id).ConfigureAwait(true);
        if (changed == false)
        {
            await _ui.OpenWindowsSettingsAsync("sound").ConfigureAwait(true);
            return;
        }

        if (changed == true)
        {
            await RefreshAsync(silent: true).ConfigureAwait(true);
        }
    }

    internal async void ToggleFavorite(AudioDeviceItemViewModel item)
    {
        var favorites = _ui.Settings.FavoriteAudioDeviceIds
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var isFavorite = !favorites.Contains(item.Id);
        if (isFavorite)
        {
            favorites.Add(item.Id);
        }
        else
        {
            favorites.Remove(item.Id);
        }

        item.SetFavorite(isFavorite);
        var updated = _ui.Settings with { FavoriteAudioDeviceIds = favorites.ToArray() };
        if (!await _ui.UpdateSettingsAsync(updated).ConfigureAwait(true))
        {
            item.SetFavorite(!isFavorite);
        }
    }

    internal async void OpenPerAppVolumeSettings()
    {
        await _ui.OpenWindowsSettingsAsync("apps-volume").ConfigureAwait(true);
    }

    internal bool IsFavoriteAudioDevice(string deviceId) =>
        _ui.Settings.FavoriteAudioDeviceIds.Contains(deviceId, StringComparer.OrdinalIgnoreCase);

    private void ToggleMute()
    {
        _ = ToggleMuteAsync();
    }

    private async Task ToggleMuteAsync()
    {
        var target = !IsMuted;
        var level = await _ui.SetMasterMuteAsync(target).ConfigureAwait(true);
        if (level is not null)
        {
            ApplyMaster(level.Volume, level.Muted, updateVolume: false);
            _lastMasterChangeUtc = DateTime.UtcNow;
        }
    }

    private async void QueueMasterVolume(double volume)
    {
        _masterDebounce?.Cancel();
        _masterDebounce?.Dispose();
        var source = new CancellationTokenSource();
        _masterDebounce = source;

        try
        {
            await Task.Delay(130, source.Token).ConfigureAwait(true);
            var level = await _ui.SetMasterVolumeAsync(volume).ConfigureAwait(true);
            if (level is not null)
            {
                ApplyMaster(level.Volume, level.Muted, updateVolume: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_masterDebounce, source))
            {
                _masterDebounce = null;
            }

            source.Dispose();
        }
    }

    private async Task RefreshAsync(bool silent = false, bool showErrors = false)
    {
        if (_refreshing)
        {
            _refreshRequested = true;
            return;
        }

        _refreshing = true;
        if (!silent && Sessions.Count == 0)
        {
            IsLoading = true;
        }

        try
        {
            var state = await _ui.GetAudioStateAsync(silent).ConfigureAwait(true);
            if (state is null)
            {
                if (!silent || showErrors)
                {
                    Error = Loc.T("Error_Audio");
                }

                return;
            }

            ApplyState(state);
        }
        finally
        {
            IsLoading = false;
            _refreshing = false;
            if (_refreshRequested)
            {
                _refreshRequested = false;
                _ = RefreshAsync(silent: true);
            }
        }
    }

    private async Task RefreshInputPeakAsync()
    {
        if (_peakRequestInProgress)
        {
            return;
        }

        _peakRequestInProgress = true;
        try
        {
            var peak = await _ui.GetDefaultInputPeakAsync().ConfigureAwait(true);
            InputPeakLevel = peak?.Level;
        }
        finally
        {
            _peakRequestInProgress = false;
        }
    }

    private void ApplyState(AudioStateDto state)
    {
        HasDevice = state.HasDevice;
        DeviceName = state.HasDevice ? state.DeviceName : Loc.T("Audio_Disabled");
        Error = state.Error
                ?? (state.HasDevice ? string.Empty : Loc.T("Audio_DisabledHint"));
        SyncDevices(OutputDevices, state.OutputDevices);
        SyncDevices(InputDevices, state.InputDevices);

        if (DateTime.UtcNow - _lastMasterChangeUtc > TimeSpan.FromSeconds(1))
        {
            ApplyMaster(state.Volume, state.Muted, updateVolume: true);
        }

        SyncSessions(state.Sessions);
        OnPropertyChanged(nameof(SessionsText));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowSessionsEmpty));
    }

    private void ApplyMaster(double volume, bool muted, bool updateVolume)
    {
        IsMuted = muted;

        if (!updateVolume)
        {
            return;
        }

        var percent = Math.Round(volume * 100);
        if (Math.Abs(percent - _masterVolume) < 1)
        {
            return;
        }

        _masterVolume = percent;
        OnPropertyChanged(nameof(MasterVolume));
        OnPropertyChanged(nameof(MasterVolumeText));
        OnPropertyChanged(nameof(IsHighVolumeWarning));
    }

    private void SyncSessions(IReadOnlyList<AudioSessionDto> sessions)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dto in sessions)
        {
            seen.Add(dto.Key);
            var existing = Sessions.FirstOrDefault(item => item.Key == dto.Key);
            if (existing is null)
            {
                Sessions.Add(new AudioSessionItemViewModel(dto, this));
            }
            else
            {
                existing.Update(dto);
            }
        }

        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(Sessions[index].Key))
            {
                Sessions.RemoveAt(index);
            }
        }
    }

    private void SyncDevices(
        ObservableCollection<AudioDeviceItemViewModel> destination,
        IReadOnlyList<AudioDeviceDto> devices)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in devices)
        {
            seen.Add(dto.Id);
            var existing = destination.FirstOrDefault(item =>
                string.Equals(item.Id, dto.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                destination.Add(new AudioDeviceItemViewModel(dto, this));
            }
            else
            {
                existing.Apply(dto, IsFavoriteAudioDevice(dto.Id));
            }
        }

        for (var index = destination.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(destination[index].Id))
            {
                destination.RemoveAt(index);
            }
        }
    }

    private void OnDeviceChanged(object? sender, string deviceName)
    {
        if (IsActive)
        {
            _ = RefreshAsync(silent: true);
        }
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        if (IsActive)
        {
            _ = RefreshAsync(silent: true);
        }
    }

    private void OnAudioSessionsChanged(object? sender, EventArgs e)
    {
        if (IsActive)
        {
            _ = RefreshAsync(silent: true);
        }
    }

    private void OnAudioChanged(object? sender, AudioChangedEvent e)
    {
        ApplyMaster(
            e.Volume,
            e.Muted,
            updateVolume: DateTime.UtcNow - _lastMasterChangeUtc > TimeSpan.FromSeconds(1));
    }

    private void OnSettingsChanged(object? sender, TrayXSettings settings)
    {
        foreach (var device in OutputDevices.Concat(InputDevices))
        {
            device.SetFavorite(IsFavoriteAudioDevice(device.Id));
        }
    }
}
