using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;

namespace TrayX.UI.ViewModels;

public sealed class NavigationItem : ObservableObject
{
    public NavigationItem(string key, string titleKey, Geometry? icon)
    {
        Key = key;
        TitleKey = titleKey;
        Icon = icon;
    }

    public string Key { get; }

    public string TitleKey { get; }

    public Geometry? Icon { get; }

    public string Title => Loc.T(TitleKey);

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));
}

public sealed class AudioSessionItemViewModel : ObservableObject
{
    private readonly AudioViewModel _owner;
    private bool _muted;
    private double _volume;

    public AudioSessionItemViewModel(AudioSessionDto dto, AudioViewModel owner)
    {
        _owner = owner;
        Key = dto.Key;
        DisplayName = dto.DisplayName;
        ProcessPath = dto.ProcessPath;
        State = dto.State;
        IsSystemSounds = dto.IsSystemSounds;
        _muted = dto.Muted;
        _volume = Math.Round(dto.Volume * 100);
        ToggleMuteCommand = new RelayCommand(() => _owner.ToggleSessionMute(this));
        OpenRoutingSettingsCommand = new RelayCommand(() => _owner.OpenPerAppVolumeSettings());
    }

    public string Key { get; }

    public string DisplayName { get; }

    public string? ProcessPath { get; }

    public string State { get; private set; }

    public bool IsSystemSounds { get; }

    public bool HasPath => !string.IsNullOrWhiteSpace(ProcessPath);

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand OpenRoutingSettingsCommand { get; }

    public DateTime LastVolumeChangeUtc { get; private set; } = DateTime.MinValue;

    public DateTime LastMuteChangeUtc { get; private set; } = DateTime.MinValue;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, Math.Clamp(value, 0, 100)))
            {
                return;
            }

            LastVolumeChangeUtc = DateTime.UtcNow;
            _owner.QueueSessionVolume(this, _volume / 100);
        }
    }

    public bool Muted
    {
        get => _muted;
        set => SetProperty(ref _muted, value);
    }

    internal void Update(AudioSessionDto dto)
    {
        if (!string.Equals(DisplayName, dto.DisplayName, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(DisplayName));
        }

        if (!string.Equals(State, dto.State, StringComparison.Ordinal))
        {
            State = dto.State;
            OnPropertyChanged(nameof(State));
        }

        Muted = dto.Muted;

        if (DateTime.UtcNow - LastVolumeChangeUtc > TimeSpan.FromSeconds(1))
        {
            var volume = Math.Round(dto.Volume * 100);
            if (Math.Abs(volume - _volume) >= 1)
            {
                _volume = volume;
                OnPropertyChanged(nameof(Volume));
            }
        }
    }

    internal void SetMuted(bool muted)
    {
        LastMuteChangeUtc = DateTime.UtcNow;
        Muted = muted;
    }
}

public sealed class AudioDeviceItemViewModel : ObservableObject
{
    private readonly AudioViewModel _owner;
    private double _volume;
    private bool _isMuted;
    private bool _isDefault;
    private bool _canControl;
    private DateTime _lastVolumeChangeUtc = DateTime.MinValue;

    public AudioDeviceItemViewModel(AudioDeviceDto dto, AudioViewModel owner)
    {
        _owner = owner;
        Id = dto.Id;
        Name = dto.Name;
        Flow = dto.Flow;
        Apply(dto, owner.IsFavoriteAudioDevice(dto.Id));
        ToggleMuteCommand = new RelayCommand(() => _ = ToggleMuteAsync());
        SetDefaultCommand = new RelayCommand(() => owner.SetDefaultDevice(this));
        ToggleFavoriteCommand = new RelayCommand(() => owner.ToggleFavorite(this));
    }

    public string Id { get; }

    public string Name { get; }

    public string Flow { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand SetDefaultCommand { get; }

    public IRelayCommand ToggleFavoriteCommand { get; }

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Round(Math.Clamp(value, 0, 100));
            if (!SetProperty(ref _volume, clamped) || !_canControl)
            {
                return;
            }

            _lastVolumeChangeUtc = DateTime.UtcNow;
            _owner.QueueDeviceVolume(this, clamped / 100);
        }
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

    public bool IsDefault
    {
        get => _isDefault;
        private set => SetProperty(ref _isDefault, value);
    }

    public bool CanControl
    {
        get => _canControl;
        private set => SetProperty(ref _canControl, value);
    }

    public bool IsFavorite { get; private set; }

    public bool CanSetDefault => !IsDefault;

    public string FavoriteTooltip => IsFavorite ? Loc.T("Audio_RemoveFavorite") : Loc.T("Audio_AddFavorite");

    public string MuteTooltip => IsMuted ? Loc.T("Header_Unmute") : Loc.T("Header_Mute");

    internal void Apply(AudioDeviceDto dto, bool isFavorite)
    {
        IsDefault = dto.IsDefault;
        OnPropertyChanged(nameof(CanSetDefault));
        if (IsFavorite != isFavorite)
        {
            IsFavorite = isFavorite;
            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(FavoriteTooltip));
        }

        CanControl = dto.CanControl && dto.Volume.HasValue;
        IsMuted = dto.IsMuted;
        if (dto.Volume.HasValue &&
            DateTime.UtcNow - _lastVolumeChangeUtc > TimeSpan.FromSeconds(1))
        {
            var volume = Math.Round(dto.Volume.Value * 100);
            if (Math.Abs(volume - _volume) >= 1)
            {
                _volume = volume;
                OnPropertyChanged(nameof(Volume));
            }
        }
    }

    internal void SetFavorite(bool isFavorite)
    {
        IsFavorite = isFavorite;
        OnPropertyChanged(nameof(IsFavorite));
        OnPropertyChanged(nameof(FavoriteTooltip));
    }

    private async Task ToggleMuteAsync()
    {
        var target = !IsMuted;
        var level = await _owner.SetDeviceMuteAsync(this, target).ConfigureAwait(true);
        if (level is not null)
        {
            IsMuted = level.Muted;
        }
    }
}

public sealed class DisplayItemViewModel : ObservableObject
{
    private readonly DisplaysViewModel _owner;
    private int? _brightness;

    public DisplayItemViewModel(DisplayDto dto, DisplaysViewModel owner)
    {
        _owner = owner;
        Apply(dto);
    }

    public int Index { get; private set; }

    public string DeviceName { get; private set; } = string.Empty;

    public string StableId { get; private set; } = string.Empty;

    public string FriendlyName { get; private set; } = string.Empty;

    public bool IsPrimary { get; private set; }

    public bool ShowTrayPanelControl => !IsPrimary;

    public bool TrayPanelEnabled =>
        !IsPrimary &&
        !_owner.IsTrayPanelDisabled(StableId, DeviceName);

    public int X { get; private set; }

    public int Y { get; private set; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public int WorkX { get; private set; }

    public int WorkY { get; private set; }

    public int WorkWidth { get; private set; }

    public int WorkHeight { get; private set; }

    public uint DpiX { get; private set; }

    public string Orientation { get; private set; } = "landscape";

    public int RefreshRate { get; private set; }

    public int BitsPerPixel { get; private set; }

    public DateTime LastBrightnessChangeUtc { get; private set; } = DateTime.MinValue;

    public string NumberText => $"{Loc.T("Displays_OverlayMonitor")} {Index + 1}";

    public string ResolutionText => $"{Width} × {Height}";

    public string RefreshText => RefreshRate > 0 ? $"{RefreshRate} Hz" : Loc.T("Common_NotAvailable");

    public string DpiText => $"{(int)Math.Round(DpiX / 96.0 * 100)}%";

    public string OrientationText => Orientation switch
    {
        "portrait" => Loc.T("Displays_OrientationPortrait"),
        "portrait-flipped" => Loc.T("Displays_OrientationPortrait"),
        "landscape-flipped" => Loc.T("Displays_OrientationLandscape"),
        _ => Loc.T("Displays_OrientationLandscape")
    };

    public string WorkAreaText => $"{WorkWidth} × {WorkHeight} @ {WorkX},{WorkY}";

    public void SetTrayPanelEnabled(bool enabled)
    {
        if (IsPrimary || TrayPanelEnabled == enabled)
        {
            return;
        }

        _ = _owner.SetTrayPanelEnabledAsync(this, enabled);
    }

    internal void RefreshTrayPanelEnabled()
    {
        OnPropertyChanged(nameof(ShowTrayPanelControl));
        OnPropertyChanged(nameof(TrayPanelEnabled));
    }

    public string PositionText => $"{X}, {Y}";

    public bool HasBrightness => _brightness.HasValue;

    public bool ShowUnsupported => !_brightness.HasValue;

    public double BrightnessValue
    {
        get => _brightness ?? 0;
        set
        {
            var rounded = Math.Round(Math.Clamp(value, 0, 100));
            if (_brightness.HasValue && Math.Abs(_brightness.Value - rounded) < 0.5)
            {
                return;
            }

            _brightness = (int)rounded;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BrightnessText));
            LastBrightnessChangeUtc = DateTime.UtcNow;
            _owner.QueueBrightness(this, (int)rounded);
        }
    }

    public string BrightnessText => _brightness.HasValue ? $"{_brightness}%" : "—";

    internal void Apply(DisplayDto dto)
    {
        var changedNumber = Index != dto.Index;
        Index = dto.Index;
        DeviceName = dto.DeviceName;
        StableId = string.IsNullOrWhiteSpace(dto.StableId) ? dto.DeviceName : dto.StableId;
        FriendlyName = dto.FriendlyName;
        IsPrimary = dto.IsPrimary;
        RefreshTrayPanelEnabled();
        X = dto.X;
        Y = dto.Y;
        Width = dto.Width;
        Height = dto.Height;
        WorkX = dto.WorkX;
        WorkY = dto.WorkY;
        WorkWidth = dto.WorkWidth;
        WorkHeight = dto.WorkHeight;
        DpiX = dto.DpiX;
        Orientation = dto.Orientation;
        RefreshRate = dto.RefreshRate;
        BitsPerPixel = dto.BitsPerPixel;

        if (DateTime.UtcNow - LastBrightnessChangeUtc > TimeSpan.FromSeconds(1) ||
            !_brightness.HasValue)
        {
            _brightness = dto.Brightness;
        }

        if (changedNumber)
        {
            OnPropertyChanged(nameof(NumberText));
        }

        OnPropertyChanged(nameof(ResolutionText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(DpiText));
        OnPropertyChanged(nameof(OrientationText));
        OnPropertyChanged(nameof(WorkAreaText));
        OnPropertyChanged(nameof(PositionText));
        OnPropertyChanged(nameof(HasBrightness));
        OnPropertyChanged(nameof(ShowUnsupported));
        OnPropertyChanged(nameof(BrightnessValue));
        OnPropertyChanged(nameof(BrightnessText));
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(NumberText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(OrientationText));
    }
}

public sealed class AppItemViewModel : ObservableObject
{
    private readonly AppsViewModel _owner;

    public AppItemViewModel(AppEntryDto dto, AppsViewModel owner)
    {
        _owner = owner;
        Entry = dto;
        LaunchCommand = new RelayCommand(() => _owner.Launch(this));
    }

    public AppEntryDto Entry { get; }

    public string Name => Entry.Name;

    public string ShortcutPath => Entry.ShortcutPath;

    public string Source => Entry.Source;

    public bool IsStoreApp => string.Equals(Entry.Source, "apps", StringComparison.OrdinalIgnoreCase);

    public string SourceLabel => IsStoreApp ? Loc.T("Apps_Store") : Loc.T("Apps_StartMenu");

    public string SourceHint => IsStoreApp ? Loc.T("Apps_StoreHint") : Loc.T("Apps_StartMenuHint");

    public IRelayCommand LaunchCommand { get; }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(SourceLabel));
        OnPropertyChanged(nameof(SourceHint));
    }
}

/// <summary>
/// Действия над значком настоящего трея: один набор используют и страница «Значки трея», и быстрая панель.
/// </summary>
public interface ITrayIconActions
{
    Task ActivateAsync(TrayLiveIconItemViewModel item);

    Task ShowContextMenuAsync(TrayLiveIconItemViewModel item);

    Task SetPromotedAsync(TrayLiveIconItemViewModel item, bool promoted);
}

public sealed class TrayLiveIconItemViewModel : ObservableObject
{
    private readonly ITrayIconActions _owner;
    private bool _isPromoted;
    private bool _suppress;
    private ImageSource? _image;

    public TrayLiveIconItemViewModel(TrayLiveIconDto dto, ITrayIconActions owner)
    {
        _owner = owner;
        Dto = dto;
        _isPromoted = dto.IsPromoted;
        ActivateCommand = new RelayCommand(() => _ = _owner.ActivateAsync(this), () => dto.CanActivate);
        ContextMenuCommand = new RelayCommand(() => _ = _owner.ShowContextMenuAsync(this), () => dto.CanActivate);
    }

    public TrayLiveIconDto Dto { get; }

    public string Id => Dto.Id;

    public string Name => Dto.Name;

    public string? RegistryKey => Dto.RegistryKey;

    public string? ExecutablePath => Dto.ExecutablePath;

    public bool IsLive => Dto.MonitorIndex >= 0;

    public bool CanActivate => Dto.CanActivate;

    public bool ShowSwitch => Dto.RegistryKey is not null;

    public bool IsRunning => Dto.IsRunning;

    public string Subtitle => !string.IsNullOrWhiteSpace(Dto.ExecutablePath)
        ? Dto.ExecutablePath
        : Dto.Owner ?? Loc.T("Tray_SystemIcon");

    public string KindText => Dto.Kind switch
    {
        TrayIconKinds.Application => Loc.T("Tray_KindApp"),
        TrayIconKinds.System => Loc.T("Tray_KindSystem"),
        TrayIconKinds.Overflow => Loc.T("Tray_KindOverflow"),
        TrayIconKinds.Hidden => Loc.T("Tray_KindHidden"),
        _ => Loc.T("Tray_KindStale")
    };

    public string MonitorText => IsLive
        ? string.Format(Loc.T("Tray_Monitor"), Dto.MonitorIndex + 1)
        : Loc.T("Tray_NotOnBar");

    public string StateText => IsLive
        ? Loc.T("Tray_VisibleNow")
        : IsRunning
            ? Loc.T("Tray_RunningHidden")
            : Loc.T("Tray_NotRunning");

    public string PositionText => IsLive
        ? $"{Dto.Left} × {Dto.Top}"
        : string.Empty;

    public ImageSource? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    public bool HasImage => _image is not null;

    public IRelayCommand ActivateCommand { get; }

    public IRelayCommand ContextMenuCommand { get; }

    public bool IsPromoted
    {
        get => _isPromoted;
        set
        {
            if (!SetProperty(ref _isPromoted, value) || _suppress)
            {
                return;
            }

            _ = _owner.SetPromotedAsync(this, value);
        }
    }

    internal void SetPromotedSilently(bool value)
    {
        _suppress = true;
        IsPromoted = value;
        _suppress = false;
    }

    internal void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(KindText));
        OnPropertyChanged(nameof(MonitorText));
        OnPropertyChanged(nameof(StateText));
    }
}

public sealed class TrayLiveSectionViewModel
{
    public TrayLiveSectionViewModel(string header, IEnumerable<TrayLiveIconItemViewModel> items)
    {
        Header = header;
        Items = items.ToList();
    }

    public string Header { get; }

    public IReadOnlyList<TrayLiveIconItemViewModel> Items { get; }

    public string CountText => Items.Count.ToString(Loc.Instance.Culture);
}

public sealed class ProfileItemViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public ProfileItemViewModel(ProfileDto dto, ProfilesViewModel owner)
    {
        _owner = owner;
        Profile = dto;
        AccentBrush = CreateBrush(dto.Accent);
        ApplyCommand = new RelayCommand(() => _owner.Apply(this));
        DeleteCommand = new RelayCommand(() => _owner.Delete(this));
    }

    public ProfileDto Profile { get; }

    public Guid Id => Profile.Id;

    public string Name => Profile.Name;

    public string VolumeText => Profile.Muted
        ? Loc.T("Profiles_Muted")
        : string.Format(Loc.T("Profiles_Volume"), Profile.VolumePercent);

    public string ThemeText => string.Equals(Profile.WindowsTheme, "light", StringComparison.OrdinalIgnoreCase)
        ? Loc.T("Profiles_Light")
        : Loc.T("Profiles_Dark");

    public string CreatedText => string.Format(
        Loc.T("Profiles_Created"),
        Profile.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", Loc.Instance.Culture));

    public Brush AccentBrush { get; }

    public IRelayCommand ApplyCommand { get; }

    public IRelayCommand DeleteCommand { get; }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(VolumeText));
        OnPropertyChanged(nameof(ThemeText));
        OnPropertyChanged(nameof(CreatedText));
    }

    private static Brush CreateBrush(string? hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(TrayXSettings.NormalizeAccent(hex));
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x7C, 0x5C, 0xFF));
            brush.Freeze();
            return brush;
        }
    }
}

public sealed class ProcessItemViewModel
{
    public ProcessItemViewModel(ProcessUsageDto dto, long totalBytes)
    {
        Name = dto.Name;
        ProcessId = dto.ProcessId;
        MemoryBytes = dto.WorkingSetBytes;
        MemoryText = string.Format(Loc.T("System_ProcessMemory"), dto.WorkingSetBytes / 1024 / 1024);
        Share = totalBytes > 0 ? dto.WorkingSetBytes * 100d / totalBytes : 0;
    }

    public string Name { get; }

    public int ProcessId { get; }

    public long MemoryBytes { get; }

    public string MemoryText { get; }

    public double Share { get; }
}

public sealed class ModuleItemViewModel
{
    public ModuleItemViewModel(ModuleStatusDto dto)
    {
        Id = dto.Id;
        DisplayName = dto.DisplayName;
        IsAvailable = dto.IsAvailable;
        IsRunning = dto.IsRunning;
        Details = dto.Details;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool IsAvailable { get; }

    public bool IsRunning { get; }

    public string? Details { get; }

    public string StatusText => !IsAvailable
        ? Loc.T("System_ModuleUnavailable")
        : IsRunning
            ? Loc.T("System_ModuleRunning")
            : Loc.T("System_ModuleStopped");

    public string StatusBrushKey => !IsAvailable
        ? "TrayX.DangerBrush"
        : IsRunning
            ? "TrayX.SuccessBrush"
            : "TrayX.WarningBrush";
}

public sealed class AccentOption : ObservableObject
{
    private bool _isSelected;

    public AccentOption(string hex)
    {
        Hex = TrayXSettings.NormalizeAccent(hex);
        var color = (Color)ColorConverter.ConvertFromString(Hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Brush = brush;
    }

    public string Hex { get; }

    public Brush Brush { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
