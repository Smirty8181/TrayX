using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class TrayAppsViewModel : PageViewModelBase
{
    private static readonly JsonSerializerOptions RuleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly UiContext _ui;
    private string _newRuleText = string.Empty;
    private TrayAppWindowDto? _selectedWindow;
    private bool _autoHideEnabled;
    private bool _isBusy;
    private bool _isLoadingWindows;
    private bool _loaded;
    private bool _dirty;

    public TrayAppsViewModel(UiContext ui)
    {
        _ui = ui;
        AddRuleCommand = new RelayCommand(AddRule);
        LoadWindowsCommand = new RelayCommand(() => _ = LoadWindowsAsync(), () => !IsBusy && !IsLoadingWindows);
        AddSelectedWindowCommand = new RelayCommand(AddSelectedWindow, () => SelectedWindow is not null && !IsBusy);
        HideSelectedWindowCommand = new RelayCommand(() => _ = HideSelectedWindowAsync(), () => SelectedWindow is not null && !IsBusy && !IsLoadingWindows);
        RestorePendingWindowsCommand = new RelayCommand(() => _ = RestorePendingWindowsAsync(), () => PendingRecoveries.Count > 0 && !IsBusy);
        RestoreRecoveryCommand = new RelayCommand<TrayAppRecoveryRecord>(
            record =>
            {
                if (record is not null)
                {
                    _ = RestoreRecoveryAsync(record);
                }
            },
            record => record is not null && !IsBusy);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => IsDirty && !IsBusy);
        ImportCommand = new RelayCommand(() => _ = ImportAsync(), () => !IsBusy);
        ExportCommand = new RelayCommand(Export, () => !IsBusy && Rules.Count > 0);
        Rules.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(ShowEmpty));
            UpdateCommands();
            MarkDirty();
        };
        _ui.SettingsChanged += (_, settings) =>
        {
            if (!_dirty)
            {
                LoadRules(settings);
            }
        };
        _ui.TrayAppsWindowsChanged += (_, _) =>
        {
            if (IsActive)
            {
                _ = LoadWindowsAsync();
            }
        };
    }

    public override string Key => "tray-apps";

    protected override string TitleKey => "TrayApps_Title";

    protected override string SubtitleKey => "TrayApps_Subtitle";

    public ObservableCollection<TrayAppRuleItemViewModel> Rules { get; } = new();

    public ObservableCollection<TrayAppWindowDto> AvailableWindows { get; } = new();

    public ObservableCollection<TrayAppRecoveryRecord> PendingRecoveries { get; } = new();

    public IRelayCommand AddRuleCommand { get; }

    public IRelayCommand LoadWindowsCommand { get; }

    public IRelayCommand AddSelectedWindowCommand { get; }

    public IRelayCommand HideSelectedWindowCommand { get; }

    public IRelayCommand RestorePendingWindowsCommand { get; }

    public IRelayCommand<TrayAppRecoveryRecord> RestoreRecoveryCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand ImportCommand { get; }

    public IRelayCommand ExportCommand { get; }

    public string NewRuleText
    {
        get => _newRuleText;
        set => SetProperty(ref _newRuleText, value);
    }

    public bool AutoHideEnabled
    {
        get => _autoHideEnabled;
        set
        {
            if (SetProperty(ref _autoHideEnabled, value))
            {
                MarkDirty();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                UpdateCommands();
            }
        }
    }

    public bool IsLoadingWindows
    {
        get => _isLoadingWindows;
        private set
        {
            if (SetProperty(ref _isLoadingWindows, value))
            {
                UpdateCommands();
            }
        }
    }

    public TrayAppWindowDto? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (SetProperty(ref _selectedWindow, value))
            {
                (AddSelectedWindowCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsDirty
    {
        get => _dirty;
        private set
        {
            if (SetProperty(ref _dirty, value))
            {
                UpdateCommands();
            }
        }
    }

    public string CountText => string.Format(Loc.T("TrayApps_Count"), Rules.Count);

    public string PendingRecoveriesText =>
        string.Format(Loc.T("TrayApps_PendingCount"), PendingRecoveries.Count);

    public bool ShowEmpty => Rules.Count == 0;

    protected override async Task OnActivatedAsync()
    {
        if (!_loaded)
        {
            LoadRules(_ui.Settings);
        }

        await LoadWindowsAsync().ConfigureAwait(true);
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(PendingRecoveriesText));
        foreach (var item in Rules)
        {
            item.RefreshLanguage();
        }
    }

    private void LoadRules(TrayXSettings settings)
    {
        Rules.Clear();
        AutoHideEnabled = settings.TrayAppsAutoHideEnabled;
        foreach (var rule in settings.TrayAppRules)
        {
            AddItem(rule);
        }

        _loaded = true;
        IsDirty = false;
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void AddRule()
    {
        try
        {
            AddItem(TrayAppRuleMatcher.ParseLegacyRule(NewRuleText));
            NewRuleText = string.Empty;
            MarkDirty();
        }
        catch (FormatException exception)
        {
            _ui.Report(exception.Message);
        }
    }

    private async Task LoadWindowsAsync()
    {
        if (IsBusy || IsLoadingWindows)
        {
            return;
        }

        IsLoadingWindows = true;
        try
        {
            var windows = await _ui.GetVisibleTrayAppWindowsAsync().ConfigureAwait(true);
            if (windows is null)
            {
                return;
            }

            AvailableWindows.Clear();
            foreach (var window in windows)
            {
                AvailableWindows.Add(window);
            }

            SelectedWindow = AvailableWindows.FirstOrDefault();
            _ui.Report(string.Format(Loc.T("TrayApps_WindowsLoaded"), AvailableWindows.Count));
            await LoadPendingRecoveriesAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoadingWindows = false;
        }
    }

    private async Task HideSelectedWindowAsync()
    {
        if (IsBusy || SelectedWindow is not { } window)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var hidden = await _ui.HideTrayAppWindowAsync(window).ConfigureAwait(true);
            if (!hidden)
            {
                _ui.Report(Loc.T("TrayApps_HideFailed"));
                return;
            }

            AvailableWindows.Remove(window);
            SelectedWindow = AvailableWindows.FirstOrDefault();
            await LoadPendingRecoveriesAsync().ConfigureAwait(true);
            _ui.Report(string.Format(Loc.T("TrayApps_WindowHidden"), window.Title));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestorePendingWindowsAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var restored = await _ui.RestorePendingTrayAppWindowsAsync().ConfigureAwait(true);
            await LoadPendingRecoveriesAsync().ConfigureAwait(true);
            var windows = await _ui.GetVisibleTrayAppWindowsAsync().ConfigureAwait(true);
            if (windows is not null)
            {
                AvailableWindows.Clear();
                foreach (var window in windows)
                {
                    AvailableWindows.Add(window);
                }

                SelectedWindow = AvailableWindows.FirstOrDefault();
            }

            _ui.Report(string.Format(
                Loc.T("TrayApps_RestoredCount"),
                restored,
                PendingRecoveries.Count));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreRecoveryAsync(TrayAppRecoveryRecord record)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var restored = await _ui.RestoreTrayAppWindowAsync(record).ConfigureAwait(true);
            await LoadPendingRecoveriesAsync().ConfigureAwait(true);
            var windows = await _ui.GetVisibleTrayAppWindowsAsync().ConfigureAwait(true);
            if (windows is not null)
            {
                AvailableWindows.Clear();
                foreach (var window in windows)
                {
                    AvailableWindows.Add(window);
                }

                SelectedWindow = AvailableWindows.FirstOrDefault();
            }

            var recoveryStillPending = PendingRecoveries.Any(pending =>
                pending.WindowHandle == record.WindowHandle &&
                pending.ProcessId == record.ProcessId &&
                pending.ProcessStartTimeUtcTicks == record.ProcessStartTimeUtcTicks);
            _ui.Report(restored > 0
                ? string.Format(Loc.T("TrayApps_WindowRestored"), record.WindowTitle)
                : recoveryStillPending
                    ? Loc.T("TrayApps_WindowRestoreFailed")
                    : string.Format(Loc.T("TrayApps_WindowAlreadyClosed"), record.WindowTitle));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadPendingRecoveriesAsync()
    {
        var pending = await _ui.GetPendingTrayAppRecoveriesAsync().ConfigureAwait(true);
        if (pending is null)
        {
            return;
        }

        PendingRecoveries.Clear();
        foreach (var record in pending)
        {
            PendingRecoveries.Add(record);
        }

        OnPropertyChanged(nameof(PendingRecoveriesText));
        (RestorePendingWindowsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RestoreRecoveryCommand as RelayCommand<TrayAppRecoveryRecord>)?.NotifyCanExecuteChanged();
    }

    private void AddSelectedWindow()
    {
        if (SelectedWindow is not { } window)
        {
            return;
        }

        var rule = new TrayAppRuleSettings
        {
            Name = Path.GetFileNameWithoutExtension(window.ProcessName),
            ProcessName = window.ProcessName,
            MatchMode = TrayAppMatchMode.Process,
            MatchAllProcessWindows = true
        };
        AddItem(TrayAppRuleMatcher.ValidateRule(rule));
        MarkDirty();
        _ui.Report(string.Format(Loc.T("TrayApps_WindowRuleAdded"), rule.Name));
    }

    private async Task SaveAsync()
    {
        if (IsBusy || !IsDirty)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var rules = Rules.Select(item => TrayAppRuleMatcher.ValidateRule(item.Rule)).ToArray();
            var settings = _ui.Settings with
            {
                TrayAppRules = rules,
                TrayAppsAutoHideEnabled = AutoHideEnabled
            };
            if (!await _ui.UpdateSettingsAsync(settings).ConfigureAwait(true))
            {
                _ui.Report(Loc.T("TrayApps_SaveFailed"));
                return;
            }

            LoadRules(_ui.Settings);
            _ui.Report(Loc.T("TrayApps_Saved"));
        }
        catch (FormatException exception)
        {
            _ui.Report(exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Filter = "JSON or Trayy rules (*.json;*.txt)|*.json;*.txt|JSON (*.json)|*.json|Text (*.txt)|*.txt",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var info = new FileInfo(dialog.FileName);
        if (info.Length > 1_048_576)
        {
            _ui.Report(Loc.T("TrayApps_FileTooLarge"));
            return;
        }

        IsBusy = true;
        try
        {
            var content = await File.ReadAllTextAsync(dialog.FileName).ConfigureAwait(true);
            var imported = string.Equals(
                Path.GetExtension(dialog.FileName),
                ".json",
                StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize<List<TrayAppRuleSettings>>(content, RuleJsonOptions)
                    ?? throw new JsonException("Файл не содержит список правил.")
                : TrayAppRuleMatcher.ParseLegacyRules(content);

            if (imported.Count > 1000)
            {
                _ui.Report(Loc.T("TrayApps_TooManyRules"));
                return;
            }

            var validated = imported.Select(TrayAppRuleMatcher.ValidateRule).ToArray();
            Rules.Clear();
            foreach (var rule in validated)
            {
                AddItem(rule);
            }

            MarkDirty();
            _ui.Report(string.Format(Loc.T("TrayApps_Imported"), Rules.Count));
        }
        catch (Exception exception) when (
            exception is JsonException or FormatException or IOException or UnauthorizedAccessException)
        {
            _ui.Report(string.Format(Loc.T("TrayApps_ImportFailed"), exception.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "JSON (*.json)|*.json|Trayy text rules (*.txt)|*.txt",
            DefaultExt = ".json",
            AddExtension = true,
            FileName = "trayx-tray-app-rules"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var rules = Rules.Select(item => TrayAppRuleMatcher.ValidateRule(item.Rule)).ToArray();
            var content = string.Equals(
                Path.GetExtension(dialog.FileName),
                ".txt",
                StringComparison.OrdinalIgnoreCase)
                ? TrayAppRuleMatcher.ExportLegacyRules(rules)
                : JsonSerializer.Serialize(rules, RuleJsonOptions);
            File.WriteAllText(dialog.FileName, content);
            _ui.Report(Loc.T("TrayApps_Exported"));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException or IOException or UnauthorizedAccessException)
        {
            _ui.Report(string.Format(Loc.T("TrayApps_ExportFailed"), exception.Message));
        }
    }

    private void AddItem(TrayAppRuleSettings rule)
    {
        var item = new TrayAppRuleItemViewModel(rule);
        item.PropertyChanged += (_, _) => MarkDirty();
        item.RemoveRequested += OnRemoveRequested;
        Rules.Add(item);
    }

    private void OnRemoveRequested(object? sender, EventArgs e)
    {
        if (sender is TrayAppRuleItemViewModel item)
        {
            Rules.Remove(item);
        }
    }

    private void MarkDirty()
    {
        if (_loaded)
        {
            IsDirty = true;
        }
    }

    private void UpdateCommands()
    {
        (SaveCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ImportCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ExportCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (LoadWindowsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (AddSelectedWindowCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (HideSelectedWindowCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RestorePendingWindowsCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (RestoreRecoveryCommand as RelayCommand<TrayAppRecoveryRecord>)?.NotifyCanExecuteChanged();
    }
}

public sealed class TrayAppRuleItemViewModel : ObservableObject
{
    private TrayAppRuleSettings _rule;

    public TrayAppRuleItemViewModel(TrayAppRuleSettings rule)
    {
        _rule = rule;
        RemoveCommand = new RelayCommand(() => RemoveRequested?.Invoke(this, EventArgs.Empty));
    }

    public event EventHandler? RemoveRequested;

    public IRelayCommand RemoveCommand { get; }

    public string Name => _rule.Name;

    public string MatchText => _rule.MatchMode switch
    {
        TrayAppMatchMode.Process => _rule.ProcessName,
        TrayAppMatchMode.WindowTitle => _rule.WindowTitle,
        TrayAppMatchMode.WindowTitleRegex => $"regex:{_rule.WindowTitle}",
        TrayAppMatchMode.WindowClass => _rule.WindowClass,
        _ => string.Empty
    };

    public string ModeText => Loc.T(_rule.MatchMode switch
    {
        TrayAppMatchMode.Process => "TrayApps_ModeProcess",
        TrayAppMatchMode.WindowTitle => "TrayApps_ModeTitle",
        TrayAppMatchMode.WindowTitleRegex => "TrayApps_ModeRegex",
        TrayAppMatchMode.WindowClass => "TrayApps_ModeClass",
        _ => "TrayApps_ModeTitle"
    });

    public string StatusText => _rule.IsExclusion
        ? Loc.T("TrayApps_Exclusion")
        : _rule.AutoHide
            ? Loc.T("TrayApps_AutoHideArmed")
            : Loc.T("TrayApps_RuleSavedOnly");

    public bool IsEnabled
    {
        get => _rule.IsEnabled;
        set
        {
            if (_rule.IsEnabled == value)
            {
                return;
            }

            _rule = _rule with { IsEnabled = value };
            OnPropertyChanged();
        }
    }

    public bool AutoHide
    {
        get => _rule.AutoHide;
        set
        {
            if (_rule.AutoHide == value)
            {
                return;
            }

            _rule = _rule with { AutoHide = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool CanAutoHide => !_rule.IsExclusion;

    public TrayAppRuleSettings Rule => _rule;

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(StatusText));
    }
}
