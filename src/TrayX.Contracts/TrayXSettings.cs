using System.Globalization;

namespace TrayX.Contracts;

public sealed record TrayXSettings
{
    private const int MaxCollectionItems = 1000;
    private const int MaxRuleNameLength = 128;
    private const int MaxProcessNameLength = 260;
    private const int MaxWindowTitleLength = 2048;
    private const int MaxWindowClassLength = 256;
    private const int MaxDeviceIdLength = 2048;
    private const int MaxDisplayIdLength = 512;
    private const int MaxSoundPathLength = 32767;

    public const int CurrentConfigVersion = 5;

    public const string DefaultAccent = "#7C5CFF";

    public int ConfigVersion { get; init; } = CurrentConfigVersion;

    public string ThemeMode { get; init; } = "dark";

    public string Accent { get; init; } = DefaultAccent;

    public string Language { get; init; } = "ru";

    public string ClockFormat { get; init; } = "system";

    public Guid? ScheduledProfileId { get; init; }

    public string ScheduledProfileTime { get; init; } = "09:00";

    public bool LaunchAtStartup { get; init; }

    public bool OpenDashboardOnStartup { get; init; }

    public bool TrayIconEnabled { get; init; } = true;

    public string TrayLeftClickAction { get; init; } = "panel";

    public bool ShowTrayNotifications { get; init; } = true;

    public bool PlayDeviceSounds { get; init; }

    public string DeviceSoundPath { get; init; } = string.Empty;

    public int MonitorIntervalMs { get; init; } = 1500;

    public bool TrayLiveEnabled { get; init; } = true;

    public int TrayLivePollMs { get; init; } = 3000;

    public IReadOnlyList<string> DisabledTrayPanelDisplays { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FavoriteAudioDeviceIds { get; init; } = Array.Empty<string>();

    public IReadOnlyList<TrayAppRuleSettings> TrayAppRules { get; init; } =
        Array.Empty<TrayAppRuleSettings>();

    public bool TrayAppsAutoHideEnabled { get; init; }

    public static TrayXSettings Default => new();

    public static string FormatClockTime(DateTime time, string? format, CultureInfo? culture = null) =>
        format switch
        {
            "12" => time.ToString("h:mm tt", culture ?? CultureInfo.CurrentCulture),
            "24" => time.ToString("HH:mm", culture ?? CultureInfo.CurrentCulture),
            _ => time.ToString("t", culture ?? CultureInfo.CurrentCulture)
        };

    public TrayXSettings Normalize()
    {
        var theme = ThemeMode is "light" or "dark" or "system" ? ThemeMode : "dark";
        var accent = NormalizeAccent(Accent);
        var language = Language is "en" ? "en" : "ru";
        var clockFormat = ClockFormat is "12" or "24" ? ClockFormat : "system";
        var scheduledTime = TimeOnly.TryParseExact(
            ScheduledProfileTime ?? string.Empty,
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedTime)
            ? parsedTime.ToString("HH:mm", CultureInfo.InvariantCulture)
            : "09:00";
        var clickAction = TrayLeftClickAction is "dashboard" or "mute" ? TrayLeftClickAction : "panel";
        var interval = Math.Clamp(MonitorIntervalMs, 500, 10000);
        var liveInterval = Math.Clamp(TrayLivePollMs, 1000, 30000);
        var trayAppRules = (TrayAppRules ?? Array.Empty<TrayAppRuleSettings>())
            .Where(rule => rule is not null && rule.Id != Guid.Empty)
            .GroupBy(rule => rule.Id)
            .Select(group =>
            {
                var rule = group.First();
                var name = (rule.Name ?? string.Empty).Trim();
                var processName = (rule.ProcessName ?? string.Empty).Trim();
                var windowTitle = (rule.WindowTitle ?? string.Empty).Trim();
                var windowClass = (rule.WindowClass ?? string.Empty).Trim();
                return rule with
                {
                    Name = Limit(name, MaxRuleNameLength),
                    ProcessName = Limit(processName, MaxProcessNameLength),
                    WindowTitle = Limit(windowTitle, MaxWindowTitleLength),
                    WindowClass = Limit(windowClass, MaxWindowClassLength),
                    MatchMode = Enum.IsDefined(rule.MatchMode) ? rule.MatchMode : TrayAppMatchMode.Process,
                    IsEnabled = rule.IsEnabled &&
                        processName.Length <= MaxProcessNameLength &&
                        windowTitle.Length <= MaxWindowTitleLength &&
                        windowClass.Length <= MaxWindowClassLength,
                    Priority = Math.Clamp(rule.Priority, -1000, 1000)
                };
            })
            .Take(MaxCollectionItems)
            .ToArray();

        return this with
        {
            ConfigVersion = Math.Max(ConfigVersion, CurrentConfigVersion),
            ThemeMode = theme,
            Accent = accent,
            Language = language,
            ClockFormat = clockFormat,
            DeviceSoundPath = NormalizeSoundPath(DeviceSoundPath),
            ScheduledProfileTime = scheduledTime,
            TrayLeftClickAction = clickAction,
            MonitorIntervalMs = interval,
            TrayLivePollMs = liveInterval,
            DisabledTrayPanelDisplays = (DisabledTrayPanelDisplays ?? Array.Empty<string>())
                .Where(display => !string.IsNullOrWhiteSpace(display))
                .Select(display => display.Trim())
                .Where(display => display.Length <= MaxDisplayIdLength)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxCollectionItems)
                .ToArray(),
            FavoriteAudioDeviceIds = (FavoriteAudioDeviceIds ?? Array.Empty<string>())
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Select(deviceId => deviceId.Trim())
                .Where(deviceId => deviceId.Length <= MaxDeviceIdLength)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxCollectionItems)
                .ToArray(),
            TrayAppRules = trayAppRules
        };
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string NormalizeSoundPath(string? path)
    {
        var normalized = (path ?? string.Empty).Trim();
        return normalized.Length <= MaxSoundPathLength ? normalized : string.Empty;
    }

    public static string NormalizeAccent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultAccent;
        }

        var candidate = value.Trim();
        if (!candidate.StartsWith('#'))
        {
            candidate = "#" + candidate;
        }

        if (candidate.Length is not (7 or 9))
        {
            return DefaultAccent;
        }

        for (var index = 1; index < candidate.Length; index++)
        {
            if (!Uri.IsHexDigit(candidate[index]))
            {
                return DefaultAccent;
            }
        }

        return candidate.ToUpperInvariant();
    }
}

public sealed record SettingsChangedEventArgs(TrayXSettings Settings);
