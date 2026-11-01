using System.Diagnostics;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;
using System.IO;

namespace TrayX.UI.ViewModels;

public sealed class ChoiceOption<T> : ObservableObject
{
    public ChoiceOption(T value, string labelKey)
    {
        Value = value;
        LabelKey = labelKey;
    }

    public T Value { get; }

    public string LabelKey { get; }

    public string Label => Loc.T(LabelKey);

    public void RefreshLabel() => OnPropertyChanged(nameof(Label));
}

public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly DialogService _dialogs;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private int _saving;
    private string _themeMode = "dark";
    private string _accent = TrayXSettings.DefaultAccent;
    private string _language = "ru";
    private string _clockFormat = "system";
    private bool _launchAtStartup;
    private bool _openDashboardOnStartup;
    private bool _trayIconEnabled = true;
    private string _trayLeftClickAction = "panel";
    private bool _showTrayNotifications = true;
    private bool _playDeviceSounds;
    private string _deviceSoundPath = string.Empty;
    private int _monitorIntervalMs = 1500;
    private bool _trayLiveEnabled = true;
    private int _trayLivePollMs = 3000;
    private string _accentHex = TrayXSettings.DefaultAccent;
    private bool _coreAvailable;

    public SettingsViewModel(UiContext ui, DialogService dialogs)
    {
        _ui = ui;
        _dialogs = dialogs;

        TrayClickOptions = new[]
        {
            new ChoiceOption<string>("panel", "Settings_TrayClickPanel"),
            new ChoiceOption<string>("dashboard", "Settings_TrayClickDashboard"),
            new ChoiceOption<string>("mute", "Settings_TrayClickMute")
        };

        ClockFormatOptions = new[]
        {
            new ChoiceOption<string>("system", "Settings_ClockSystem"),
            new ChoiceOption<string>("12", "Settings_Clock12"),
            new ChoiceOption<string>("24", "Settings_Clock24")
        };

        IntervalOptions = new[]
        {
            new ChoiceOption<int>(500, "Settings_IntervalFast"),
            new ChoiceOption<int>(1000, "Settings_Interval1"),
            new ChoiceOption<int>(1500, "Settings_Interval15"),
            new ChoiceOption<int>(3000, "Settings_Interval3"),
            new ChoiceOption<int>(5000, "Settings_Interval5"),
            new ChoiceOption<int>(10000, "Settings_Interval10")
        };

        LiveIntervalOptions = new[]
        {
            new ChoiceOption<int>(1000, "Settings_Interval1"),
            new ChoiceOption<int>(2000, "Settings_Interval2"),
            new ChoiceOption<int>(3000, "Settings_Interval3"),
            new ChoiceOption<int>(5000, "Settings_Interval5"),
            new ChoiceOption<int>(10000, "Settings_Interval10"),
            new ChoiceOption<int>(30000, "Settings_Interval30")
        };

        Accents = new[]
        {
            new AccentOption("#7C5CFF"),
            new AccentOption("#FF3F94"),
            new AccentOption("#3B82F6"),
            new AccentOption("#22D3EE"),
            new AccentOption("#3DDC97"),
            new AccentOption("#FFB65C"),
            new AccentOption("#FF5C7A")
        };

        SetThemeCommand = new RelayCommand<string>(SetTheme);
        SetLanguageCommand = new RelayCommand<string>(SetLanguage);
        SelectAccentCommand = new RelayCommand<AccentOption>(SelectAccent);
        ApplyAccentCommand = new RelayCommand(ApplyCustomAccent);
        OpenWindowsSettingsCommand = new RelayCommand<string>(page =>
        {
            if (!string.IsNullOrWhiteSpace(page))
            {
                _ = _ui.OpenWindowsSettingsAsync(page);
            }
        });
        OpenDataFolderCommand = new RelayCommand(() => OpenFolder(DataFolder));
        OpenLogsCommand = new RelayCommand(() => OpenFolder(LogsFolder));
        SelectDeviceSoundCommand = new RelayCommand(SelectDeviceSound);
        ClearDeviceSoundCommand = new RelayCommand(() => _ = SetDeviceSoundPathAsync(string.Empty));
        PreviewDeviceSoundCommand = new RelayCommand(() => _ = PreviewDeviceSoundAsync());
        ExportSettingsCommand = new RelayCommand(() => _ = ExportSettings());
        ImportSettingsCommand = new RelayCommand(() => _ = ImportSettings());
        ResetSettingsCommand = new RelayCommand(() => _ = ResetSettings());
        ExitCommand = new RelayCommand(() => _ = ExitAsync());

        _ui.ConnectionChanged += (_, _) =>
        {
            IsCoreAvailable = _ui.IsConnected;
        };
        _ui.SettingsChanged += (_, _) =>
        {
            if (Volatile.Read(ref _saving) == 0)
            {
                SyncFromSettings();
            }
        };

        SyncFromSettings();
    }

    public override string Key => "settings";

    protected override string TitleKey => "Settings_Title";

    protected override string SubtitleKey => "Settings_Subtitle";

    public event EventHandler? ExitRequested;

    public IReadOnlyList<ChoiceOption<string>> TrayClickOptions { get; }

    public IReadOnlyList<ChoiceOption<string>> ClockFormatOptions { get; }

    public IReadOnlyList<ChoiceOption<int>> IntervalOptions { get; }

    public IReadOnlyList<ChoiceOption<int>> LiveIntervalOptions { get; }

    public IReadOnlyList<AccentOption> Accents { get; }

    public IRelayCommand<string> SetThemeCommand { get; }

    public IRelayCommand<string> SetLanguageCommand { get; }

    public IRelayCommand<AccentOption> SelectAccentCommand { get; }

    public IRelayCommand ApplyAccentCommand { get; }

    public IRelayCommand<string> OpenWindowsSettingsCommand { get; }

    public IRelayCommand OpenDataFolderCommand { get; }

    public IRelayCommand OpenLogsCommand { get; }

    public IRelayCommand SelectDeviceSoundCommand { get; }

    public IRelayCommand ClearDeviceSoundCommand { get; }

    public IRelayCommand PreviewDeviceSoundCommand { get; }

    public IRelayCommand ExportSettingsCommand { get; }

    public IRelayCommand ImportSettingsCommand { get; }

    public IRelayCommand ResetSettingsCommand { get; }

    public IRelayCommand ExitCommand { get; }

    public bool IsThemeDark => _themeMode == "dark";

    public bool IsThemeLight => _themeMode == "light";

    public bool IsThemeSystem => _themeMode == "system";

    public bool IsLanguageRu => _language == "ru";

    public bool IsLanguageEn => _language == "en";

    public bool IsCoreAvailable
    {
        get => _coreAvailable;
        private set => SetProperty(ref _coreAvailable, value);
    }

    public string AccentHex
    {
        get => _accentHex;
        set => SetProperty(ref _accentHex, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set
        {
            if (SetProperty(ref _launchAtStartup, value))
            {
                _ = SaveAsync(value ? Loc.T("Settings_AutostartOn") : Loc.T("Settings_AutostartOff"));
            }
        }
    }

    public bool OpenDashboardOnStartup
    {
        get => _openDashboardOnStartup;
        set
        {
            if (SetProperty(ref _openDashboardOnStartup, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public bool TrayIconEnabled
    {
        get => _trayIconEnabled;
        set
        {
            if (SetProperty(ref _trayIconEnabled, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public bool ShowTrayNotifications
    {
        get => _showTrayNotifications;
        set
        {
            if (SetProperty(ref _showTrayNotifications, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public bool PlayDeviceSounds
    {
        get => _playDeviceSounds;
        set
        {
            if (SetProperty(ref _playDeviceSounds, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public string DeviceSoundPathText => string.IsNullOrWhiteSpace(_deviceSoundPath)
        ? Loc.T("Settings_SoundDefault")
        : _deviceSoundPath;

    public bool TrayLiveEnabled
    {
        get => _trayLiveEnabled;
        set
        {
            if (SetProperty(ref _trayLiveEnabled, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public int TrayLivePollMs
    {
        get => _trayLivePollMs;
        set
        {
            if (SetProperty(ref _trayLivePollMs, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public string TrayLeftClickAction
    {
        get => _trayLeftClickAction;
        set
        {
            if (_trayLeftClickAction == value || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (SetProperty(ref _trayLeftClickAction, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public string ClockFormat
    {
        get => _clockFormat;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || _clockFormat == value)
            {
                return;
            }

            if (SetProperty(ref _clockFormat, value))
            {
                _ = SaveAsync();
            }
        }
    }

    public int MonitorIntervalMs
    {
        get => _monitorIntervalMs;
        set
        {
            var clamped = Math.Clamp(value, 500, 10000);
            if (_monitorIntervalMs == clamped)
            {
                return;
            }

            if (SetProperty(ref _monitorIntervalMs, clamped))
            {
                _ = SaveAsync();
            }
        }
    }

    public string AppVersionText => "TrayX " + FormatVersion(Assembly.GetExecutingAssembly().GetName().Version);

    public string CoreVersionText => _ui.CoreStatus?.Version ?? "—";

    public string ProtocolVersionText => _ui.CoreStatus is { } status
        ? $"v{status.ProtocolVersion}"
        : "—";

    public string WindowsBuildText => _ui.CoreStatus?.WindowsBuild.ToString(Loc.Instance.Culture) ?? "—";

    private static string DataFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TrayX");

    private static string LogsFolder => Path.Combine(DataFolder, "logs");

    protected override async Task OnActivatedAsync()
    {
        SyncFromSettings();
        IsCoreAvailable = _ui.IsConnected;

        var status = await _ui.GetStatusAsync(silent: true).ConfigureAwait(true);
        if (status is not null)
        {
            OnPropertyChanged(nameof(CoreVersionText));
            OnPropertyChanged(nameof(ProtocolVersionText));
            OnPropertyChanged(nameof(WindowsBuildText));
        }
    }

    protected override void OnLanguageChanged()
    {
        foreach (var option in TrayClickOptions)
        {
            option.RefreshLabel();
        }

        foreach (var option in IntervalOptions)
        {
            option.RefreshLabel();
        }

        foreach (var option in LiveIntervalOptions)
        {
            option.RefreshLabel();
        }

        foreach (var option in ClockFormatOptions)
        {
            option.RefreshLabel();
        }

        OnPropertyChanged(nameof(WindowsBuildText));
        OnPropertyChanged(nameof(DeviceSoundPathText));
    }

    private void SetTheme(string? mode)
    {
        var normalized = mode is "light" or "system" ? mode : "dark";
        if (_themeMode == normalized)
        {
            return;
        }

        _themeMode = normalized;
        OnPropertyChanged(nameof(IsThemeDark));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeSystem));
        _ = SaveAsync();
    }

    private void SetLanguage(string? language)
    {
        var normalized = language == "en" ? "en" : "ru";
        if (_language == normalized)
        {
            return;
        }

        _language = normalized;
        OnPropertyChanged(nameof(IsLanguageRu));
        OnPropertyChanged(nameof(IsLanguageEn));
        _ = SaveAsync();
    }

    private void SelectAccent(AccentOption? option)
    {
        if (option is null)
        {
            return;
        }

        SetAccent(option.Hex);
    }

    private void ApplyCustomAccent()
    {
        var candidate = AccentHex.Trim();
        if (candidate.StartsWith('#'))
        {
            candidate = candidate[1..];
        }

        if (candidate.Length is not (6 or 8) || !candidate.All(Uri.IsHexDigit))
        {
            _ui.Report(Loc.T("Settings_AccentInvalid"));
            return;
        }

        SetAccent("#" + candidate);
    }

    private void SetAccent(string hex)
    {
        var normalized = TrayXSettings.NormalizeAccent(hex);
        _accent = normalized;
        AccentHex = normalized;
        UpdateAccentSelection();
        _ = SaveAsync();
    }

    private Task SaveAsync(string? successMessage = null)
    {
        return SaveCoreAsync(successMessage);
    }

    private async Task SaveCoreAsync(string? successMessage)
    {
        await _saveGate.WaitAsync().ConfigureAwait(true);
        Interlocked.Increment(ref _saving);

        try
        {
            var settings = BuildSettings().Normalize();
            var result = await _ui.UpdateSettingsAsync(settings).ConfigureAwait(true);
            if (!result)
            {
                _ui.Report(Loc.T("Settings_SaveFailed"));
                SyncFromSettings(force: true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(successMessage))
            {
                _ui.Report(successMessage);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _saving);
            _saveGate.Release();
        }
    }

    private TrayXSettings BuildSettings()
    {
        return _ui.Settings with
        {
            ThemeMode = _themeMode,
            Accent = _accent,
            Language = _language,
            ClockFormat = _clockFormat,
            LaunchAtStartup = _launchAtStartup,
            OpenDashboardOnStartup = _openDashboardOnStartup,
            TrayIconEnabled = _trayIconEnabled,
            TrayLeftClickAction = _trayLeftClickAction,
            ShowTrayNotifications = _showTrayNotifications,
            PlayDeviceSounds = _playDeviceSounds,
            DeviceSoundPath = _deviceSoundPath,
            MonitorIntervalMs = _monitorIntervalMs,
            TrayLiveEnabled = _trayLiveEnabled,
            TrayLivePollMs = _trayLivePollMs
        };
    }

    private void SyncFromSettings(bool force = false)
    {
        if (!force && Volatile.Read(ref _saving) > 0)
        {
            return;
        }

        var settings = _ui.Settings.Normalize();
        _themeMode = settings.ThemeMode;
        _accent = settings.Accent;
        _language = settings.Language;
        _clockFormat = settings.ClockFormat;
        _launchAtStartup = settings.LaunchAtStartup;
        _openDashboardOnStartup = settings.OpenDashboardOnStartup;
        _trayIconEnabled = settings.TrayIconEnabled;
        _trayLeftClickAction = settings.TrayLeftClickAction;
        _showTrayNotifications = settings.ShowTrayNotifications;
        _playDeviceSounds = settings.PlayDeviceSounds;
        _deviceSoundPath = settings.DeviceSoundPath;
        _monitorIntervalMs = settings.MonitorIntervalMs;
        _trayLiveEnabled = settings.TrayLiveEnabled;
        _trayLivePollMs = settings.TrayLivePollMs;
        _accentHex = settings.Accent;

        UpdateAccentSelection();
        OnPropertyChanged(string.Empty);
    }

    private void UpdateAccentSelection()
    {
        foreach (var option in Accents)
        {
            option.IsSelected = string.Equals(option.Hex, _accent, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task ExitAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Settings_ExitConfirmTitle"),
            Loc.T("Settings_ExitConfirm")).ConfigureAwait(true);

        if (confirmed)
        {
            ExitRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _ui.Report(Loc.T("Settings_FolderError"));
        }
    }

    private void SelectDeviceSound()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = $"{Loc.T("Settings_SoundFile")} (*.wav)|*.wav",
            Multiselect = false
        };
        if (dialog.ShowDialog() == true)
        {
            _ = SetDeviceSoundPathAsync(dialog.FileName);
        }
    }

    private async Task SetDeviceSoundPathAsync(string path)
    {
        var updated = _ui.Settings with { DeviceSoundPath = path };
        if (!await _ui.UpdateSettingsAsync(updated).ConfigureAwait(true))
        {
            _ui.Report(Loc.T("Settings_SaveFailed"));
            return;
        }

        _deviceSoundPath = updated.Normalize().DeviceSoundPath;
        OnPropertyChanged(nameof(DeviceSoundPathText));
    }

    private async Task PreviewDeviceSoundAsync()
    {
        var played = await _ui.PreviewDeviceSoundAsync(_deviceSoundPath).ConfigureAwait(true);
        _ui.Report(Loc.T(played ? "Settings_SoundPreviewPlayed" : "Settings_SoundPreviewFailed"));
    }

    private async Task ExportSettings()
    {
        var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = ".json",
            FileName = "trayx-settings.json",
            Filter = $"{Loc.T("Settings_ConfigFile")} (*.json)|*.json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, TrayXSettingsJson.Serialize(_ui.Settings))
                .ConfigureAwait(true);
            _ui.Report(Loc.T("Settings_ConfigExported"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _ui.Report($"{Loc.T("Settings_ConfigExportFailed")} {exception.Message}");
        }
    }

    private async Task ImportSettings()
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true,
            Filter = $"{Loc.T("Settings_ConfigFile")} (*.json)|*.json",
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var file = new FileInfo(dialog.FileName);
            if (file.Length > 1024 * 1024)
            {
                _ui.Report(Loc.T("Settings_ConfigTooLarge"));
                return;
            }

            var imported = TrayXSettingsJson.Deserialize(
                await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true));
            var confirmed = await _dialogs.ConfirmAsync(
                Loc.T("Settings_ConfigImportTitle"),
                Loc.T("Settings_ConfigImportConfirm")).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            if (!await _ui.UpdateSettingsAsync(imported).ConfigureAwait(true))
            {
                _ui.Report(Loc.T("Settings_SaveFailed"));
                return;
            }

            SyncFromSettings(force: true);
            _ui.Report(Loc.T("Settings_ConfigImported"));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
                System.Text.Json.JsonException or ArgumentException)
        {
            _ui.Report($"{Loc.T("Settings_ConfigImportFailed")} {exception.Message}");
        }
    }

    private async Task ResetSettings()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Settings_ConfigResetTitle"),
            Loc.T("Settings_ConfigResetConfirm")).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        if (!await _ui.UpdateSettingsAsync(TrayXSettings.Default).ConfigureAwait(true))
        {
            _ui.Report(Loc.T("Settings_SaveFailed"));
            return;
        }

        SyncFromSettings(force: true);
        _ui.Report(Loc.T("Settings_ConfigReset"));
    }

    private static string FormatVersion(Version? version) =>
        version is null ? "dev" : $"v{version.Major}.{version.Minor}.{version.Build}";
}
