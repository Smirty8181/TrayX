using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class TrayIconsViewModel : PageViewModelBase, ITrayIconActions
{
    private readonly UiContext _ui;
    private readonly DialogService _dialogs;
    private readonly List<TrayLiveIconItemViewModel> _all = new();

    private bool _loading;
    private bool _loaded;
    private string _search = string.Empty;
    private string _groupMode = "state";
    private string _sortMode = "position";
    private bool _overflowOpen;
    private bool _liveAvailable = true;
    private string? _notice;

    public TrayIconsViewModel(UiContext ui, DialogService dialogs)
    {
        _ui = ui;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(force: true));
        RestartExplorerCommand = new RelayCommand(() => _ = RestartExplorerAsync());
        OpenSettingsCommand = new RelayCommand(() => _ = _ui.OpenWindowsSettingsAsync("taskbar"));
        ToggleOverflowCommand = new RelayCommand(() => _ = ToggleOverflowAsync());

        GroupOptions = new[]
        {
            new ChoiceOption<string>("state", "Tray_GroupState"),
            new ChoiceOption<string>("monitor", "Tray_GroupMonitor"),
            new ChoiceOption<string>("none", "Tray_GroupNone")
        };

        SortOptions = new[]
        {
            new ChoiceOption<string>("position", "Tray_SortPosition"),
            new ChoiceOption<string>("name", "Tray_SortName"),
            new ChoiceOption<string>("status", "Tray_SortStatus")
        };

        _ui.TrayChanged += (_, _) =>
        {
            if (IsActive)
            {
                _ = LoadAsync(force: true);
            }
        };

        _ui.TrayLiveChanged += (_, snapshot) =>
        {
            if (IsActive)
            {
                Apply(snapshot);
            }
        };
    }

    public override string Key => "tray";

    protected override string TitleKey => "Tray_Title";

    protected override string SubtitleKey => "Tray_Subtitle";

    public ObservableCollection<TrayLiveSectionViewModel> Sections { get; } = new();

    public IReadOnlyList<ChoiceOption<string>> GroupOptions { get; }

    public IReadOnlyList<ChoiceOption<string>> SortOptions { get; }

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand RestartExplorerCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    public IRelayCommand ToggleOverflowCommand { get; }

    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public bool ShowEmpty => !IsLoading && _loaded && Sections.Count == 0;

    public bool ShowNotice => !string.IsNullOrWhiteSpace(NoticeText);

    public bool OverflowOpen
    {
        get => _overflowOpen;
        private set => SetProperty(ref _overflowOpen, value);
    }

    public bool LiveAvailable
    {
        get => _liveAvailable;
        private set
        {
            if (SetProperty(ref _liveAvailable, value))
            {
                OnPropertyChanged(nameof(NoticeText));
                OnPropertyChanged(nameof(ShowNotice));
            }
        }
    }

    public string NoticeText => _notice switch
    {
        TrayNotices.UiaUnavailable => Loc.T("Tray_UiaFailed"),
        TrayNotices.LiveDisabled => Loc.T("Tray_LiveDisabled"),
        _ => string.Empty
    };

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value))
            {
                ApplyFilter();
            }
        }
    }

    public string GroupMode
    {
        get => _groupMode;
        set
        {
            if (SetProperty(ref _groupMode, value))
            {
                ApplyFilter();
            }
        }
    }

    public string SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                ApplyFilter();
            }
        }
    }

    public string CountText => string.Format(
        Loc.T("Tray_Count"),
        _all.Count,
        _all.Count(item => item.IsPromoted),
        _all.Count(item => item.IsLive));

    protected override async Task OnActivatedAsync()
    {
        if (!_loaded)
        {
            await LoadAsync(force: false).ConfigureAwait(true);
        }
    }

    protected override void OnLanguageChanged()
    {
        foreach (var option in GroupOptions.Concat(SortOptions))
        {
            option.RefreshLabel();
        }

        foreach (var item in _all)
        {
            item.RefreshLanguage();
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(NoticeText));
        ApplyFilter();
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
        OnPropertyChanged(nameof(CountText));
        _ui.Report(Loc.T("Tray_PromotedNote"));
    }

    public async Task ActivateAsync(TrayLiveIconItemViewModel item)
    {
        var activated = await _ui.ActivateTrayIconAsync(item.Id).ConfigureAwait(true);
        _ui.Report(activated ? Loc.T("Tray_Activated") : Loc.T("Tray_ActionFailed"));
        if (activated)
        {
            await LoadAsync(force: true).ConfigureAwait(true);
        }
    }

    public async Task ShowContextMenuAsync(TrayLiveIconItemViewModel item)
    {
        var shown = await _ui.ShowTrayIconContextMenuAsync(item.Id).ConfigureAwait(true);
        _ui.Report(shown ? Loc.T("Tray_ContextMenuShown") : Loc.T("Tray_ActionFailed"));
    }

    private async Task ToggleOverflowAsync()
    {
        var toggled = await _ui.ToggleTrayOverflowAsync().ConfigureAwait(true);
        if (!toggled)
        {
            _ui.Report(Loc.T("Tray_OverflowFailed"));
        }

        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task RestartExplorerAsync()
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Tray_RestartConfirmTitle"),
            Loc.T("Tray_RestartConfirm")).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        _ui.Report(Loc.T("Status_ExplorerRestarting"));
        var restarted = await _ui.RestartExplorerAsync().ConfigureAwait(true);
        if (restarted)
        {
            _ui.Report(Loc.T("Status_ExplorerRestarted"));
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading || (_loaded && !force))
        {
            return;
        }

        IsLoading = true;
        try
        {
            var snapshot = await _ui.GetTraySnapshotAsync().ConfigureAwait(true);
            if (snapshot is null)
            {
                _ui.Report(Loc.T("Error_TrayIcons"));
                return;
            }

            Apply(snapshot);
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private void Apply(TraySnapshotDto snapshot)
    {
        LiveAvailable = snapshot.LiveAvailable;
        _notice = snapshot.Notice;
        OverflowOpen = snapshot.OverflowOpen;
        OnPropertyChanged(nameof(NoticeText));
        OnPropertyChanged(nameof(ShowNotice));

        _all.Clear();
        foreach (var icon in snapshot.Icons)
        {
            _all.Add(new TrayLiveIconItemViewModel(icon, this));
        }

        ApplyFilter();
        _ = TrayIconImageLoader.LoadAsync(_ui, _all);
    }

    private void ApplyFilter()
    {
        var query = _search.Trim();
        var matched = _all.Where(item =>
            query.Length == 0 ||
            item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (item.ExecutablePath?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false));

        Sections.Clear();
        foreach (var section in BuildSections(matched))
        {
            Sections.Add(section);
        }

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    private IEnumerable<TrayLiveSectionViewModel> BuildSections(IEnumerable<TrayLiveIconItemViewModel> matched)
    {
        return _groupMode switch
        {
            "monitor" => matched
                .GroupBy(item => item.IsLive ? item.Dto.MonitorIndex : -1)
                .OrderBy(group => group.Key)
                .Select(group => new TrayLiveSectionViewModel(
                    group.Key < 0
                        ? Loc.T("Tray_NotOnBar")
                        : string.Format(Loc.T("Tray_Monitor"), group.Key + 1),
                    Order(group))),
            "none" => new[] { new TrayLiveSectionViewModel(string.Empty, Order(matched)) },
            _ => matched
                .GroupBy(StateGroup)
                .OrderBy(group => group.Key.Order)
                .Select(group => new TrayLiveSectionViewModel(Loc.T(group.Key.TitleKey), Order(group)))
        };
    }

    private static (int Order, string TitleKey) StateGroup(TrayLiveIconItemViewModel item) =>
        !item.IsLive
            ? (item.IsRunning ? (1, "Tray_KindHidden") : (2, "Tray_KindStale"))
            : item.Dto.OnBar ? (0, "Tray_OnBar") : (1, "Tray_KindOverflow");

    private IEnumerable<TrayLiveIconItemViewModel> Order(IEnumerable<TrayLiveIconItemViewModel> items)
    {
        return _sortMode switch
        {
            "name" => items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            "status" => items
                .OrderByDescending(item => item.IsLive)
                .ThenByDescending(item => item.IsRunning)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items
                .OrderBy(item => item.Dto.MonitorIndex)
                .ThenBy(item => item.Dto.Top)
                .ThenBy(item => item.Dto.Left)
        };
    }
}
