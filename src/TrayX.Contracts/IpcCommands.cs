namespace TrayX.Contracts;

public static class IpcCommands
{
    public const string StatusGet = "status.get";

    public const string SettingsGet = "settings.get";
    public const string SettingsUpdate = "settings.update";

    public const string AudioGetState = "audio.state";
    public const string AudioGetInputPeak = "audio.input-peak";
    public const string AudioSetVolume = "audio.volume";
    public const string AudioSetMute = "audio.mute";
    public const string AudioDeviceSetVolume = "audio.device.volume";
    public const string AudioDeviceSetMute = "audio.device.mute";
    public const string AudioDeviceSetDefault = "audio.device.default";
    public const string AudioSetSessionVolume = "audio.session.volume";
    public const string AudioSetSessionMute = "audio.session.mute";

    public const string DisplaysGetAll = "displays.list";
    public const string DisplaysSetBrightness = "displays.brightness";
    public const string DisplaysOpenSettings = "displays.open-settings";

    public const string SystemGetStats = "system.stats";

    public const string TrayGetIcons = "tray.icons";
    public const string TraySetPromoted = "tray.promote";
    public const string TrayRestartExplorer = "tray.restart-explorer";

    public const string TrayLiveGet = "tray.live";
    public const string TrayLiveActivate = "tray.activate";
    public const string TrayLiveContextMenu = "tray.context-menu";
    public const string TrayOverflowToggle = "tray.overflow";
    public const string TrayIconImage = "tray.icon-image";

    public const string AppsGetAll = "apps.list";
    public const string AppsLaunch = "apps.launch";

    public const string ProfilesGetAll = "profiles.list";
    public const string ProfilesSave = "profiles.save";
    public const string ProfilesApply = "profiles.apply";
    public const string ProfilesDelete = "profiles.delete";

    public const string NotificationsGetRecent = "notifications.recent";
    public const string NotificationsClear = "notifications.clear";
    public const string TrayAppsGetWindows = "tray-apps.windows";
    public const string TrayAppsGetPending = "tray-apps.pending";
    public const string TrayAppsHideWindow = "tray-apps.hide";
    public const string TrayAppsRestoreWindow = "tray-apps.restore-window";
    public const string TrayAppsRestorePending = "tray-apps.restore-pending";

    public const string SoundPreview = "sound.preview";

    public const string WindowsThemeSet = "windows.theme";
    public const string WindowsThemeGet = "windows.theme.get";
    public const string WindowsSettingsOpen = "windows.settings";
    public const string WindowsClassicSoundOpen = "windows.sound.classic";

    public const string UiActivate = "ui.activate";
    public const string UiPing = "ui.ping";

    public const string CoreShutdown = "core.shutdown";
}

public static class IpcEvents
{
    public const string UiOpen = "ui.open";
    public const string UiActivate = "ui.activate";
    public const string SettingsChanged = "settings.changed";
    public const string AudioChanged = "audio.changed";
    public const string AudioDeviceChanged = "audio.device-changed";
    public const string AudioDevicesChanged = "audio.devices-changed";
    public const string AudioSessionsChanged = "audio.sessions-changed";
    public const string TrayChanged = "tray.changed";
    public const string TrayLiveChanged = "tray.live-changed";
    public const string ProfileApplied = "profile.applied";
    public const string WindowsThemeChanged = "windows.theme-changed";
    public const string CoreShutdown = "core.shutdown";
    public const string Toast = "toast";
    public const string NotificationHistoryChanged = "notifications.changed";
    public const string TrayAppsWindowsChanged = "tray-apps.windows-changed";
}
