using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly UiContext _ui;
    private readonly DispatcherTimer _toastTimer;
    private readonly Dictionary<string, PageViewModelBase> _pages;

    private NavigationItem? _selectedNavigation;
    private PageViewModelBase? _currentPage;
    private string _connectionPhase = "connecting";
    private bool _isMuted;
    private bool _isWindowsDark = true;
    private string _modulesText = "—";
    private string _toastMessage = string.Empty;
    private bool _toastVisible;
    private bool _refreshingConnection;

    public MainWindowViewModel(UiContext ui, DialogService dialogs)
    {
        _ui = ui;

        Dashboard = new DashboardViewModel(ui);
        Audio = new AudioViewModel(ui);
        Displays = new DisplaysViewModel(ui);
        Apps = new AppsViewModel(ui);
        TrayIcons = new TrayIconsViewModel(ui, dialogs);
        TrayApps = new TrayAppsViewModel(ui);
        Profiles = new ProfilesViewModel(ui, dialogs);
        Notifications = new NotificationsViewModel(ui, dialogs);
        Windhawk = new WindhawkViewModel();
        SystemMonitor = new SystemViewModel(ui);
        Settings = new SettingsViewModel(ui, dialogs);

        _pages = new Dictionary<string, PageViewModelBase>(StringComparer.OrdinalIgnoreCase)
        {
            [Dashboard.Key] = Dashboard,
            [Audio.Key] = Audio,
            [Displays.Key] = Displays,
            [Apps.Key] = Apps,
            [TrayIcons.Key] = TrayIcons,
            [TrayApps.Key] = TrayApps,
            [Profiles.Key] = Profiles,
            [Notifications.Key] = Notifications,
            [Windhawk.Key] = Windhawk,
            [SystemMonitor.Key] = SystemMonitor,
            [Settings.Key] = Settings
        };

        Navigation = new ObservableCollection<NavigationItem>
        {
            new("dashboard", "Nav_Dashboard", Icon("Icon.Dashboard")),
            new("audio", "Nav_Audio", Icon("Icon.Audio")),
            new("displays", "Nav_Displays", Icon("Icon.Displays")),
            new("apps", "Nav_Apps", Icon("Icon.Apps")),
            new("tray", "Nav_Tray", Icon("Icon.Tray")),
            new("tray-apps", "Nav_TrayApps", Icon("Icon.Apps")),
            new("profiles", "Nav_Profiles", Icon("Icon.Profiles")),
            new("notifications", "Nav_Notifications", Icon("Icon.Notifications")),
            new("windhawk", "Nav_Windhawk", Icon("Icon.Settings")),
            new("system", "Nav_System", Icon("Icon.System")),
            new("settings", "Nav_Settings", Icon("Icon.Settings"))
        };

        ToggleMuteCommand = new RelayCommand(() => _ = ToggleMuteAsync());
        ToggleWindowsThemeCommand = new RelayCommand(() => _ = ToggleWindowsThemeAsync());
        OpenSettingsCommand = new RelayCommand(() => Navigate("settings"));
        OpenQuickPanelCommand = new RelayCommand(() => QuickPanelRequested?.Invoke(this, EventArgs.Empty));
        ReconnectCommand = new RelayCommand(() => _ = ReconnectAsync());

        Dashboard.NavigationRequested += (_, page) => Navigate(page);
        Settings.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.6) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastVisible = false;
        };

        _ui.Message += (_, message) => ShowToast(message);
        _ui.ConnectionChanged += (_, _) =>
        {
            ConnectionPhase = _ui.IsConnected ? "online" : "offline";
            if (_ui.IsConnected)
            {
                _ = RefreshConnectionAsync();
            }
        };
        _ui.AudioChanged += (_, changed) => IsMuted = changed.Muted;
        _ui.WindowsThemeChanged += (_, theme) =>
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
        };
        _ui.SettingsChanged += (_, _) => RefreshNavigationTitles();

        Loc.Instance.LanguageChanged += (_, _) =>
        {
            RefreshNavigationTitles();
            OnPropertyChanged(nameof(Tagline));
            OnPropertyChanged(nameof(MuteTooltip));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(ModulesText));
            OnPropertyChanged(nameof(VersionText));
        };

        _selectedNavigation = Navigation[0];
        CurrentPage = Dashboard;
    }

    public event EventHandler? QuickPanelRequested;

    public event EventHandler? ExitRequested;

    public DashboardViewModel Dashboard { get; }

    public AudioViewModel Audio { get; }

    public DisplaysViewModel Displays { get; }

    public AppsViewModel Apps { get; }

    public TrayIconsViewModel TrayIcons { get; }

    public TrayAppsViewModel TrayApps { get; }

    public ProfilesViewModel Profiles { get; }

    public NotificationsViewModel Notifications { get; }

    public WindhawkViewModel Windhawk { get; }

    public SystemViewModel SystemMonitor { get; }

    public SettingsViewModel Settings { get; }

    public ObservableCollection<NavigationItem> Navigation { get; }

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand ToggleWindowsThemeCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    public IRelayCommand OpenQuickPanelCommand { get; }

    public IRelayCommand ReconnectCommand { get; }

    public string Tagline => Loc.T("App_Tagline");

    public NavigationItem? SelectedNavigation
    {
        get => _selectedNavigation;
        set
        {
            if (Equals(_selectedNavigation, value) || value is null)
            {
                return;
            }

            _selectedNavigation = value;
            OnPropertyChanged();
            Activate(value.Key);
        }
    }

    public PageViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string ConnectionPhase
    {
        get => _connectionPhase;
        private set
        {
            if (!SetProperty(ref _connectionPhase, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsConnecting));
            OnPropertyChanged(nameof(IsOffline));
            OnPropertyChanged(nameof(ConnectionText));
            OnPropertyChanged(nameof(StatusHint));
        }
    }

    public bool IsConnected => ConnectionPhase == "online";

    public bool IsConnecting => ConnectionPhase == "connecting";

    public bool IsOffline => ConnectionPhase == "offline";

    public string ConnectionText => ConnectionPhase switch
    {
        "online" => Loc.T("Shell_Connected"),
        "offline" => Loc.T("Shell_Offline"),
        _ => Loc.T("Shell_Connecting")
    };

    public string StatusHint => IsOffline ? Loc.T("Shell_CoreOfflineHint") : ModulesText;

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

    public bool IsWindowsDark
    {
        get => _isWindowsDark;
        private set
        {
            if (SetProperty(ref _isWindowsDark, value))
            {
                OnPropertyChanged(nameof(ThemeTooltip));
            }
        }
    }

    public string ThemeTooltip => IsWindowsDark
        ? Loc.T("Header_ThemeToLight")
        : Loc.T("Header_ThemeToDark");

    public string ModulesText
    {
        get => _modulesText;
        private set
        {
            if (SetProperty(ref _modulesText, value))
            {
                OnPropertyChanged(nameof(StatusHint));
            }
        }
    }

    public string VersionText
    {
        get
        {
            var version = _ui.CoreStatus?.Version;
            if (string.IsNullOrWhiteSpace(version))
            {
                var assembly = Assembly.GetExecutingAssembly().GetName().Version;
                version = assembly is null ? "dev" : $"{assembly.Major}.{assembly.Minor}.{assembly.Build}";
            }

            return string.Format(Loc.T("Shell_Version"), version);
        }
    }

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public void Navigate(string page)
    {
        if (string.Equals(page, "panel", StringComparison.OrdinalIgnoreCase))
        {
            QuickPanelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_pages.ContainsKey(page))
        {
            page = "dashboard";
        }

        var navigation = Navigation.First(item => item.Key == page);
        if (ReferenceEquals(_selectedNavigation, navigation))
        {
            if (CurrentPage is not null)
            {
                _ = CurrentPage.ActivateAsync();
            }

            return;
        }

        _selectedNavigation = navigation;
        OnPropertyChanged(nameof(SelectedNavigation));
        Activate(navigation.Key);
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastMessage = message;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    public async Task InitializeAsync()
    {
        ConnectionPhase = _ui.IsConnected ? "online" : "offline";
        if (_ui.IsConnected)
        {
            await RefreshConnectionAsync().ConfigureAwait(true);
        }
    }

    public void SuspendCurrentPage()
    {
        CurrentPage?.Deactivate();
    }

    public void ResumeCurrentPage()
    {
        if (CurrentPage is not null)
        {
            _ = CurrentPage.ActivateAsync();
        }
    }

    private void Activate(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            return;
        }

        if (CurrentPage is not null && !ReferenceEquals(CurrentPage, page))
        {
            CurrentPage.Deactivate();
        }

        CurrentPage = page;
        _ = page.ActivateAsync();
    }

    private async Task RefreshConnectionAsync()
    {
        if (_refreshingConnection)
        {
            return;
        }

        _refreshingConnection = true;
        try
        {
            var status = await _ui.GetStatusAsync(silent: true).ConfigureAwait(true);
            if (status is not null)
            {
                ApplyStatus(status);
            }

            var audio = await _ui.GetAudioStateAsync(silent: true).ConfigureAwait(true);
            if (audio is not null)
            {
                IsMuted = audio.Muted;
            }

            var theme = await _ui.GetWindowsThemeAsync(silent: true).ConfigureAwait(true);
            if (theme is not null)
            {
                IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
            }

            OnPropertyChanged(nameof(VersionText));
        }
        finally
        {
            _refreshingConnection = false;
        }
    }

    private void ApplyStatus(CoreStatusDto status)
    {
        var running = status.Modules.Count(module => module.IsRunning);
        ModulesText = string.Format(Loc.T("Shell_Modules"), running, status.Modules.Count);
        OnPropertyChanged(nameof(VersionText));
    }

    private async Task ToggleMuteAsync()
    {
        var level = await _ui.SetMasterMuteAsync(!IsMuted).ConfigureAwait(true);
        if (level is not null)
        {
            IsMuted = level.Muted;
        }
    }

    private async Task ToggleWindowsThemeAsync()
    {
        var mode = IsWindowsDark ? "light" : "dark";
        var theme = await _ui.SetWindowsThemeAsync(mode).ConfigureAwait(true);
        if (theme is not null)
        {
            IsWindowsDark = string.Equals(theme.Mode, "dark", StringComparison.OrdinalIgnoreCase);
            ShowToast(Loc.T("Status_WindowsThemeChanged"));
        }
    }

    private async Task ReconnectAsync()
    {
        ConnectionPhase = "connecting";
        var connected = await _ui.ReconnectAsync().ConfigureAwait(true);
        ConnectionPhase = connected ? "online" : "offline";
        if (connected)
        {
            await RefreshConnectionAsync().ConfigureAwait(true);
            ShowToast(Loc.T("Status_Refreshed"));
        }
        else
        {
            ShowToast(Loc.T("Shell_CoreOfflineHint"));
        }
    }

    private void RefreshNavigationTitles()
    {
        foreach (var item in Navigation)
        {
            item.RefreshTitle();
        }
    }

    private static Geometry? Icon(string key) => Application.Current?.TryFindResource(key) as Geometry;
}
