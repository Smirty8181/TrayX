using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class AppsViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly List<AppItemViewModel> _all = new();

    private bool _loading;
    private bool _loaded;
    private string _search = string.Empty;
    private string _sourceFilter = "all";
    private string _error = string.Empty;

    public AppsViewModel(UiContext ui)
    {
        _ui = ui;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(force: true));
        SetFilterCommand = new RelayCommand<string>(SetFilter);
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CountText));
    }

    public override string Key => "apps";

    protected override string TitleKey => "Apps_Title";

    protected override string SubtitleKey => "Apps_Subtitle";

    public ObservableCollection<AppItemViewModel> Items { get; } = new();

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand<string> SetFilterCommand { get; }

    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public bool HasItems => Items.Count > 0;

    public bool ShowEmpty => !IsLoading && Items.Count == 0 && _loaded;

    public string CountText => string.Format(Loc.T("Apps_Count"), Items.Count);

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

    public bool IsFilterAll => _sourceFilter == "all";

    public bool IsFilterStart => _sourceFilter == "start";

    public bool IsFilterStore => _sourceFilter == "apps";

    protected override async Task OnActivatedAsync()
    {
        if (!_loaded)
        {
            await LoadAsync(force: false).ConfigureAwait(true);
        }
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CountText));
        foreach (var item in _all)
        {
            item.RefreshLanguage();
        }
    }

    internal async void Launch(AppItemViewModel item)
    {
        var launched = await _ui.LaunchAppAsync(item.Entry).ConfigureAwait(true);
        if (launched)
        {
            _ui.Report(string.Format(Loc.T("Apps_Launching"), item.Name));
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading || (_loaded && !force))
        {
            return;
        }

        IsLoading = true;
        Error = string.Empty;

        try
        {
            var apps = await _ui.GetAppsAsync().ConfigureAwait(true);
            if (apps is null)
            {
                Error = Loc.T("Error_Apps");
                return;
            }

            _all.Clear();
            foreach (var app in apps.OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                _all.Add(new AppItemViewModel(app, this));
            }

            _loaded = true;
            ApplyFilter();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private void SetFilter(string? filter)
    {
        var value = filter is "start" or "apps" ? filter : "all";
        if (_sourceFilter == value)
        {
            return;
        }

        _sourceFilter = value;
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterStart));
        OnPropertyChanged(nameof(IsFilterStore));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var query = _search.Trim();
        var filtered = _all.Where(item =>
            (_sourceFilter == "all" || item.Source == _sourceFilter) &&
            (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)));

        Items.Clear();
        foreach (var item in filtered)
        {
            Items.Add(item);
        }

        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(CountText));
    }
}
