using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class ProfilesViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly DialogService _dialogs;
    private bool _loading;
    private bool _loaded;
    private ProfileItemViewModel? _scheduledProfile;
    private string _scheduleTime = "09:00";

    public ProfilesViewModel(UiContext ui, DialogService dialogs)
    {
        _ui = ui;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(force: true));
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        SaveScheduleCommand = new RelayCommand(() => _ = SaveScheduleAsync());
        DisableScheduleCommand = new RelayCommand(() => _ = DisableScheduleAsync());
        _ui.SettingsChanged += (_, settings) => SyncSchedule(settings);
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(ScheduleSummary));
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CountText));
        };
    }

    public override string Key => "profiles";

    protected override string TitleKey => "Profiles_Title";

    protected override string SubtitleKey => "Profiles_Subtitle";

    public ObservableCollection<ProfileItemViewModel> Items { get; } = new();

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand SaveCommand { get; }

    public IRelayCommand SaveScheduleCommand { get; }

    public IRelayCommand DisableScheduleCommand { get; }

    public ProfileItemViewModel? ScheduledProfile
    {
        get => _scheduledProfile;
        set
        {
            if (SetProperty(ref _scheduledProfile, value))
            {
                OnPropertyChanged(nameof(ScheduleSummary));
            }
        }
    }

    public string ScheduleTime
    {
        get => _scheduleTime;
        set => SetProperty(ref _scheduleTime, value);
    }

    public string ScheduleSummary
    {
        get
        {
            if (_ui.Settings.ScheduledProfileId is not { } id)
            {
                return Loc.T("Profiles_ScheduleDisabled");
            }

            var profile = Items.FirstOrDefault(item => item.Id == id);
            return profile is null
                ? Loc.T("Profiles_ScheduleMissing")
                : string.Format(
                    Loc.T("Profiles_ScheduleSummary"),
                    profile.Name,
                    _ui.Settings.ScheduledProfileTime);
        }
    }

    public bool IsLoading
    {
        get => _loading;
        private set => SetProperty(ref _loading, value);
    }

    public bool HasProfiles => Items.Count > 0;

    public bool ShowEmpty => !IsLoading && _loaded && Items.Count == 0;

    public string CountText => string.Format(Loc.T("Profiles_Count"), Items.Count);

    protected override async Task OnActivatedAsync()
    {
        await LoadAsync(force: false).ConfigureAwait(true);
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CountText));
        foreach (var item in Items)
        {
            item.RefreshLanguage();
        }
    }

    internal async void Apply(ProfileItemViewModel item)
    {
        var profile = await _ui.ApplyProfileAsync(item.Id).ConfigureAwait(true);
        if (profile is not null)
        {
            _ui.Report(string.Format(
                Loc.T(profile.AudioApplied == false
                    ? "Profiles_AppliedWithoutAudio"
                    : "Profiles_Applied"),
                profile.Name));
        }
        else
        {
            _ui.Report(Loc.T("Error_ProfileApply"));
        }
    }

    internal async void Delete(ProfileItemViewModel item)
    {
        var confirmed = await _dialogs.ConfirmAsync(
            Loc.T("Profiles_DeleteConfirmTitle"),
            string.Format(Loc.T("Profiles_DeleteConfirm"), item.Name)).ConfigureAwait(true);

        if (!confirmed)
        {
            return;
        }

        var deleted = await _ui.DeleteProfileAsync(item.Id).ConfigureAwait(true);
        if (deleted)
        {
            _ui.Report(Loc.T("Status_ProfileDeleted"));
            await LoadAsync(force: true).ConfigureAwait(true);
        }
        else
        {
            _ui.Report(Loc.T("Error_ProfileDelete"));
        }
    }

    private async Task SaveAsync()
    {
        var name = await _dialogs.PromptAsync(
            Loc.T("Profiles_Save"),
            Loc.T("Profiles_Name"),
            string.Empty).ConfigureAwait(true);

        if (name is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            _ui.Report(Loc.T("Profiles_NameRequired"));
            return;
        }

        var profile = await _ui.SaveProfileAsync(name).ConfigureAwait(true);
        if (profile is null)
        {
            _ui.Report(Loc.T("Error_ProfileSave"));
            return;
        }

        _ui.Report(string.Format(Loc.T("Profiles_Saved"), profile.Name));
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task SaveScheduleAsync()
    {
        if (ScheduledProfile is null)
        {
            _ui.Report(Loc.T("Profiles_ScheduleSelect"));
            return;
        }

        if (!TimeOnly.TryParseExact(
                ScheduleTime,
                "HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            _ui.Report(Loc.T("Profiles_ScheduleInvalidTime"));
            return;
        }

        var updated = _ui.Settings with
        {
            ScheduledProfileId = ScheduledProfile.Id,
            ScheduledProfileTime = time.ToString("HH:mm", CultureInfo.InvariantCulture)
        };
        if (!await _ui.UpdateSettingsAsync(updated).ConfigureAwait(true))
        {
            _ui.Report(Loc.T("Profiles_ScheduleSaveFailed"));
            return;
        }

        ScheduleTime = updated.ScheduledProfileTime;
        _ui.Report(Loc.T("Profiles_ScheduleSaved"));
    }

    private async Task DisableScheduleAsync()
    {
        var updated = _ui.Settings with { ScheduledProfileId = null };
        if (!await _ui.UpdateSettingsAsync(updated).ConfigureAwait(true))
        {
            _ui.Report(Loc.T("Profiles_ScheduleSaveFailed"));
            return;
        }

        ScheduledProfile = null;
        _ui.Report(Loc.T("Profiles_ScheduleDisabled"));
    }

    private void SyncSchedule(TrayX.Contracts.TrayXSettings settings)
    {
        ScheduleTime = settings.ScheduledProfileTime;
        ScheduledProfile = settings.ScheduledProfileId is { } id
            ? Items.FirstOrDefault(profile => profile.Id == id)
            : null;
        OnPropertyChanged(nameof(ScheduleSummary));
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
            var profiles = await _ui.GetProfilesAsync().ConfigureAwait(true);
            if (profiles is null)
            {
                _ui.Report(Loc.T("Error_Profiles"));
                return;
            }

            Items.Clear();
            foreach (var profile in profiles)
            {
                Items.Add(new ProfileItemViewModel(profile, this));
            }

            _loaded = true;
            SyncSchedule(_ui.Settings);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(CountText));
        }
    }
}
