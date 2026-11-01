using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using CommunityToolkit.Mvvm.Input;
using TrayX.UI.Localization;

namespace TrayX.UI.ViewModels;

public sealed class WindhawkViewModel : PageViewModelBase
{
    private readonly ObservableCollection<WindhawkModViewModel> _mods = [];
    private bool _isInstalled;
    private bool _isRunning;
    private bool _statusAvailable = true;
    private string? _version;

    public WindhawkViewModel()
    {
        Mods = new ReadOnlyObservableCollection<WindhawkModViewModel>(_mods);
        OpenWindhawkCommand = new RelayCommand(() => OpenUrl("https://windhawk.net/"));
        RefreshStatusCommand = new RelayCommand(RefreshStatus);
        AddMod("Windhawk_TaskbarStyler", "https://windhawk.net/mods/windows-11-taskbar-styler",
            "explorer.exe", "medium");
        AddMod("Windhawk_ShowAllTrayIcons", "https://windhawk.net/mods/taskbar-notification-icons-show-all",
            "explorer.exe", "low");
        AddMod("Windhawk_StartMenuStyler", "https://windhawk.net/mods/windows-11-start-menu-styler",
            "StartMenuExperienceHost.exe, SearchHost.exe", "medium");
        AddMod("Windhawk_NotificationStyler",
            "https://windhawk.net/mods/windows-11-notification-center-styler",
            "ShellExperienceHost.exe, ShellHost.exe", "medium");
        AddMod("Windhawk_ExplorerStyler", "https://windhawk.net/mods/windows-11-file-explorer-styler",
            "explorer.exe", "medium");
        AddMod("Windhawk_TaskbarLabels", "https://windhawk.net/mods/taskbar-labels",
            "explorer.exe", "medium");
        RefreshStatus();
    }

    public override string Key => "windhawk";

    protected override string TitleKey => "Windhawk_Title";

    protected override string SubtitleKey => "Windhawk_Subtitle";

    public ReadOnlyObservableCollection<WindhawkModViewModel> Mods { get; }

    public IRelayCommand OpenWindhawkCommand { get; }

    public IRelayCommand RefreshStatusCommand { get; }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public bool StatusAvailable
    {
        get => _statusAvailable;
        private set
        {
            if (SetProperty(ref _statusAvailable, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string? Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public string StatusText
    {
        get
        {
            if (!StatusAvailable)
            {
                return Loc.T("Windhawk_StatusUnknown");
            }

            var status = IsInstalled ? Loc.T("Windhawk_Installed") : Loc.T("Windhawk_NotInstalled");
            if (IsRunning)
            {
                status += " · " + Loc.T("Windhawk_Running");
            }

            if (!string.IsNullOrWhiteSpace(Version))
            {
                status += $" · {string.Format(Loc.T("Windhawk_Version"), Version)}";
            }

            return status;
        }
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        foreach (var mod in Mods)
        {
            mod.RefreshLanguage();
        }
    }

    private void AddMod(string nameKey, string url, string process, string risk)
    {
        _mods.Add(new WindhawkModViewModel(nameKey, url, process, risk, OpenUrl));
    }

    private void RefreshStatus()
    {
        try
        {
            var installation = FindInstallation();
            var processes = Process.GetProcessesByName("windhawk");
            IsRunning = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            Version = installation.Version;
            IsInstalled = installation.Found || IsRunning;
            StatusAvailable = true;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or IOException or System.Security.SecurityException or Win32Exception)
        {
            IsInstalled = false;
            IsRunning = false;
            Version = null;
            StatusAvailable = false;
        }
    }

    private static (bool Found, string? Version) FindInstallation()
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null)
                {
                    continue;
                }

                foreach (var subKeyName in uninstall.GetSubKeyNames())
                {
                    using var entry = uninstall.OpenSubKey(subKeyName);
                    var displayName = entry?.GetValue("DisplayName") as string;
                    if (displayName?.Contains("Windhawk", StringComparison.OrdinalIgnoreCase) != true)
                    {
                        continue;
                    }

                    return (true, entry?.GetValue("DisplayVersion") as string);
                }
            }
        }

        return (false, null);
    }

    private static void OpenUrl(string url)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            System.Windows.MessageBox.Show(
                string.Format(Loc.T("Windhawk_OpenFailed"), exception.Message),
                Loc.T("Common_Error"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }
}

public sealed class WindhawkModViewModel : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private readonly string _nameKey;
    private readonly string _url;
    private readonly string _process;
    private readonly string _risk;

    public WindhawkModViewModel(string nameKey, string url, string process, string risk, Action<string> openUrl)
    {
        _nameKey = nameKey;
        _url = url;
        _process = process;
        _risk = risk;
        OpenCommand = new RelayCommand(() => openUrl(_url));
    }

    public string Name => Loc.T(_nameKey);

    public string Description => Loc.T(_nameKey switch
    {
        "Windhawk_TaskbarStyler" => "Windhawk_TaskbarStyler_Description",
        "Windhawk_ShowAllTrayIcons" => "Windhawk_ShowAllTrayIcons_Description",
        "Windhawk_StartMenuStyler" => "Windhawk_StartMenuStyler_Description",
        "Windhawk_NotificationStyler" => "Windhawk_NotificationStyler_Description",
        "Windhawk_ExplorerStyler" => "Windhawk_ExplorerStyler_Description",
        "Windhawk_TaskbarLabels" => "Windhawk_TaskbarLabels_Description",
        _ => "Windhawk_TaskbarStyler_Description"
    });

    public string Process => _process;

    public string ProcessText => string.Format(Loc.T("Windhawk_Process"), Process);

    public string Risk => Loc.T(_risk == "low" ? "Windhawk_Risk_low" : "Windhawk_Risk_medium");

    public IRelayCommand OpenCommand { get; }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(ProcessText));
        OnPropertyChanged(nameof(Risk));
    }
}
