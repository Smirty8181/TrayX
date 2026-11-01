using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TrayX.Contracts;
using TrayX.UI.Localization;
using TrayX.UI.Services;

namespace TrayX.UI.ViewModels;

public sealed class NotificationsViewModel : PageViewModelBase
{
    private readonly UiContext _ui;
    private readonly DialogService _dialogs;
    private bool _loading;
    private bool _loaded;
    private bool _reloadRequested;
    private bool _changedWhileInactive;
    private bool _clearing;

    public NotificationsViewModel(UiContext ui, DialogService dialogs)
    {
        _ui = ui;
        _dialogs = dialogs;
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(force: true));
        ClearCommand = new RelayCommand(() => _ = ClearAsync());
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasNotifications));
            OnPropertyChanged(nameof(CanClear));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(CountText));
        };
        _ui.NotificationHistoryChanged += (_, _) =>
        {
            if (IsActive)
            {
                _ = LoadAsync(force: true);
            }
            else
            {
                _changedWhileInactive = true;
            }
        };
        _ui.ConnectionChanged += (_, _) =>
        {
            _changedWhileInactive = true;
            if (IsActive && _ui.IsConnected)
            {
                _ = LoadAsync(force: true);
            }
        };
    }

    public override string Key => "notifications";

    protected override string TitleKey => "Notifications_Title";

    protected override string SubtitleKey => "Notifications_Subtitle";

    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();

    public IRelayCommand RefreshCommand { get; }

    public IRelayCommand ClearCommand { get; }

    public bool IsLoading
    {
        get => _loading;
        private set
        {
            if (SetProperty(ref _loading, value))
            {
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(CanClear));
            }
        }
    }

    public bool HasNotifications => Items.Count > 0;

    public bool CanClear => HasNotifications && !IsLoading && !IsClearing;

    public bool IsClearing
    {
        get => _clearing;
        private set
        {
            if (SetProperty(ref _clearing, value))
            {
                OnPropertyChanged(nameof(CanClear));
            }
        }
    }

    public bool ShowEmpty => !IsLoading && _loaded && Items.Count == 0;

    public string CountText => string.Format(Loc.T("Notifications_Count"), Items.Count);

    protected override async Task OnActivatedAsync()
    {
        await LoadAsync(force: !_loaded || _changedWhileInactive).ConfigureAwait(true);
    }

    protected override void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(CountText));
        foreach (var item in Items)
        {
            item.RefreshLanguage();
        }
    }

    private async Task LoadAsync(bool force)
    {
        if (IsLoading || (_loaded && !force))
        {
            _reloadRequested |= IsLoading && force;
            return;
        }

        IsLoading = true;
        try
        {
            while (true)
            {
                _reloadRequested = false;
                var notifications = await _ui.GetRecentNotificationsAsync().ConfigureAwait(true);
                if (notifications is null)
                {
                    _ui.Report(Loc.T("Notifications_LoadFailed"));
                    if (!_reloadRequested)
                    {
                        break;
                    }

                    continue;
                }

                Items.Clear();
                foreach (var notification in notifications)
                {
                    Items.Add(new NotificationItemViewModel(notification));
                }

                _loaded = true;
                _changedWhileInactive = false;
                if (!_reloadRequested)
                {
                    break;
                }
            }
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(HasNotifications));
            OnPropertyChanged(nameof(CanClear));
            OnPropertyChanged(nameof(CountText));
        }
    }

    private async Task ClearAsync()
    {
        if (!CanClear)
        {
            return;
        }

        IsClearing = true;
        try
        {
            var confirmed = await _dialogs.ConfirmAsync(
                Loc.T("Notifications_ClearTitle"),
                Loc.T("Notifications_ClearConfirm")).ConfigureAwait(true);
            if (!confirmed)
            {
                return;
            }

            if (!await _ui.ClearNotificationHistoryAsync().ConfigureAwait(true))
            {
                _ui.Report(Loc.T("Notifications_ClearFailed"));
                return;
            }

            Items.Clear();
            _loaded = true;
            _ui.Report(Loc.T("Notifications_Cleared"));
        }
        finally
        {
            IsClearing = false;
        }
    }
}

public sealed class NotificationItemViewModel : ObservableObject
{
    private readonly NotificationDto _notification;

    public NotificationItemViewModel(NotificationDto notification)
    {
        _notification = notification;
    }

    public string Title => _notification.Title;

    public string Message => _notification.Message;

    public string CreatedText => _notification.CreatedAt.ToLocalTime().ToString(
        "g",
        Loc.Instance.Culture);

    public void RefreshLanguage() => OnPropertyChanged(nameof(CreatedText));
}
