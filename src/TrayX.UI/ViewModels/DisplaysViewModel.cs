using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class DisplaysViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<string, CancellationTokenSource> _brightnessDebounce =
        new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<DisplayDto> _snapshot = Array.Empty<DisplayDto>();
    private bool _refreshing;
    private bool _isLoading;

    public DisplaysViewModel(UiContext ui)
    {
        _ui = ui;
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(showErrors: true));
        IdentifyCommand = new RelayCommand(Identify);
        OpenSettingsCommand = new RelayCommand(() => _ = _ui.OpenDisplaySettingsAsync());
        _ui.SettingsChanged += (_, _) =>
        {
            foreach (var display in Displays)
            {
                display.RefreshTrayPanelEnabled();
            }
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => _ = RefreshAsync(silent: true);
    }

    public override string Key => "displays";

    protected override string TitleKey => "Displays_Title";

    protected override string SubtitleKey => "Displays_Subtitle";

    public ObservableCollection<DisplayItemViewModel> Displays { get; } = new();

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand IdentifyCommand { get; }

    public IRelayCommand OpenSettingsCommand { get; }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool HasDisplays => Displays.Count > 0;

    public string CountText => string.Format(Loc.T("Displays_Count"), Displays.Count);

    internal bool IsTrayPanelDisabled(string stableId, string deviceName) =>
        _ui.Settings.DisabledTrayPanelDisplays.Contains(stableId, StringComparer.OrdinalIgnoreCase) ||
        _ui.Settings.DisabledTrayPanelDisplays.Contains(deviceName, StringComparer.OrdinalIgnoreCase);

    internal async Task SetTrayPanelEnabledAsync(DisplayItemViewModel display, bool enabled)
    {
        var disabled = _ui.Settings.DisabledTrayPanelDisplays
            .Where(name =>
                !string.Equals(name, display.StableId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(name, display.DeviceName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!enabled)
        {
            disabled.Add(string.IsNullOrWhiteSpace(display.StableId) ? display.DeviceName : display.StableId);
        }

        var updated = _ui.Settings with { DisabledTrayPanelDisplays = disabled };
        if (!await _ui.UpdateSettingsAsync(updated).ConfigureAwait(true))
        {
            display.RefreshTrayPanelEnabled();
            _ui.Report(Loc.T("Settings_SaveFailed"));
        }
    }

    protected override async Task OnActivatedAsync()
    {
        _timer.Start();
        await RefreshAsync(showErrors: false).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _timer.Stop();
        foreach (var source in _brightnessDebounce.Values)
        {
            source.Cancel();
        }

        _brightnessDebounce.Clear();
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CountText));
        foreach (var display in Displays)
        {
            display.RefreshLanguage();
        }
    }

    internal async void QueueBrightness(DisplayItemViewModel item, int percent)
    {
        var stableId = item.StableId;
        if (_brightnessDebounce.TryGetValue(stableId, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        var source = new CancellationTokenSource();
        _brightnessDebounce[stableId] = source;

        try
        {
            await Task.Delay(140, source.Token).ConfigureAwait(true);
            var current = Displays.FirstOrDefault(display =>
                string.Equals(display.StableId, stableId, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                return;
            }

            var displays = await _ui.SetBrightnessAsync(current.Index, percent, stableId).ConfigureAwait(true);
            if (displays is not null)
            {
                ApplyDisplays(displays);
            }
            else
            {
                await RefreshAsync(silent: true).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (_brightnessDebounce.TryGetValue(stableId, out var current) && ReferenceEquals(current, source))
            {
                _brightnessDebounce.Remove(stableId);
            }

            source.Dispose();
        }
    }

    private void Identify()
    {
        if (_snapshot.Count == 0)
        {
            _ui.Report(Loc.T("Displays_NoDisplays"));
            return;
        }

        MonitorIdentifyService.Show(_snapshot);
        _ui.Report(string.Format(Loc.T("Displays_IdentifyStarted"), 3));
    }

    private async Task RefreshAsync(bool silent = false, bool showErrors = false)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        if (!silent && Displays.Count == 0)
        {
            IsLoading = true;
        }

        try
        {
            var displays = await _ui.GetDisplaysAsync(silent).ConfigureAwait(true);
            if (displays is not null)
            {
                ApplyDisplays(displays);
            }
            else if (showErrors)
            {
                _ui.Report(Loc.T("Displays_NoDisplays"));
            }
        }
        finally
        {
            IsLoading = false;
            _refreshing = false;
        }
    }

    private void ApplyDisplays(IReadOnlyList<DisplayDto> displays)
    {
        _snapshot = displays;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dto in displays)
        {
            var stableId = string.IsNullOrWhiteSpace(dto.StableId) ? dto.DeviceName : dto.StableId;
            seen.Add(stableId);
            var existing = Displays.FirstOrDefault(item =>
                string.Equals(item.StableId, stableId, StringComparison.OrdinalIgnoreCase) ||
                (string.IsNullOrWhiteSpace(item.StableId) &&
                 string.Equals(item.DeviceName, dto.DeviceName, StringComparison.OrdinalIgnoreCase)));
            if (existing is null)
            {
                Displays.Add(new DisplayItemViewModel(dto, this));
            }
            else
            {
                existing.Apply(dto);
            }
        }

        for (var index = Displays.Count - 1; index >= 0; index--)
        {
            if (!seen.Contains(Displays[index].StableId))
            {
                Displays.RemoveAt(index);
            }
        }

        OnPropertyChanged(nameof(HasDisplays));
        OnPropertyChanged(nameof(CountText));
    }
}
