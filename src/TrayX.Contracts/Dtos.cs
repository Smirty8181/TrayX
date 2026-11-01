namespace TrayX.Contracts;

public sealed record ModuleStatusDto(
    string Id,
    string DisplayName,
    bool IsAvailable,
    bool IsRunning,
    string? Details);

public sealed record CoreStatusDto(
    string Version,
    int ProtocolVersion,
    DateTimeOffset StartedAt,
    int WindowsBuild,
    string UserName,
    IReadOnlyList<ModuleStatusDto> Modules);

public sealed record AudioDeviceDto(
    string Id,
    string Name,
    string Flow,
    bool IsDefault,
    bool IsMuted,
    double? Volume,
    bool CanControl);

public sealed record AudioSessionDto(
    string Key,
    string DisplayName,
    int ProcessId,
    string? ProcessPath,
    double Volume,
    bool Muted,
    string State,
    bool IsSystemSounds);

public sealed record AudioStateDto(
    bool HasDevice,
    string DeviceId,
    string DeviceName,
    double Volume,
    bool Muted,
    IReadOnlyList<AudioDeviceDto> OutputDevices,
    IReadOnlyList<AudioDeviceDto> InputDevices,
    IReadOnlyList<AudioSessionDto> Sessions,
    string? Error);

public sealed record AudioLevelDto(double Volume, bool Muted);

public sealed record AudioPeakDto(double Level);

public sealed record DisplayDto(
    int Index,
    string DeviceName,
    string FriendlyName,
    int X,
    int Y,
    int Width,
    int Height,
    int WorkX,
    int WorkY,
    int WorkWidth,
    int WorkHeight,
    bool IsPrimary,
    uint DpiX,
    uint DpiY,
    string Orientation,
    int RefreshRate,
    int BitsPerPixel,
    int? Brightness,
    string StableId = "");

public sealed record ProcessUsageDto(string Name, long WorkingSetBytes, int ProcessId);

public sealed record SystemStatsDto(
    DateTimeOffset Timestamp,
    double CpuPercent,
    long MemoryTotalBytes,
    long MemoryUsedBytes,
    double MemoryPercent,
    double NetworkInBytesPerSecond,
    double NetworkOutBytesPerSecond,
    int? BatteryPercent,
    bool? BatteryCharging,
    long UptimeSeconds,
    int ProcessCount,
    long SystemDriveFreeBytes,
    long SystemDriveTotalBytes,
    IReadOnlyList<ProcessUsageDto> TopProcesses);

public sealed record TrayIconDto(
    string Key,
    string Name,
    string ExecutablePath,
    bool IsPromoted,
    string? Tooltip,
    string Source);

/// <summary>
/// Вид значка в снимке трея: значение передаётся между процессами как строка.
/// </summary>
public static class TrayIconKinds
{
    public const string Application = "app";
    public const string System = "system";
    public const string Overflow = "overflow";
    public const string Hidden = "hidden";
    public const string Stale = "stale";
}

/// <summary>
/// Коды состояний в снимке трея: интерфейс сам подбирает для них текст на нужном языке.
/// </summary>
public static class TrayNotices
{
    public const string UiaUnavailable = "uia-unavailable";
    public const string LiveDisabled = "live-disabled";
}

public sealed record TrayLiveIconDto(
    string Id,
    string Name,
    string Kind,
    int MonitorIndex,
    bool OnBar,
    bool CanActivate,
    bool IsPromoted,
    bool IsRunning,
    int ProcessId,
    string? Owner,
    string? ExecutablePath,
    string? RegistryKey,
    int Left,
    int Top,
    int Width,
    int Height);

// Notice — код TrayNotices или null; локализованный текст подбирает интерфейс.
public sealed record TraySnapshotDto(
    DateTimeOffset ScannedAt,
    bool LiveAvailable,
    bool OverflowOpen,
    string? Notice,
    IReadOnlyList<TrayLiveIconDto> Icons);

public sealed record TrayIconImageDto(string Key, string? ImageBase64);

public sealed record AppEntryDto(
    string Id,
    string Name,
    string ShortcutPath,
    string? TargetPath,
    string Source);

public sealed record ProfileDto(
    Guid Id,
    string Name,
    int VolumePercent,
    bool Muted,
    string WindowsTheme,
    string Accent,
    DateTimeOffset CreatedAt,
    bool? AudioApplied = null);

public sealed record WindowsThemeDto(string Mode);

public sealed record UiOpenRequest(string Page);


public sealed record SetVolumeRequest(double Volume);

public sealed record SetMuteRequest(bool Muted);

public sealed record AudioDeviceVolumeRequest(string DeviceId, double Volume);

public sealed record AudioDeviceMuteRequest(string DeviceId, bool Muted);

public sealed record AudioDeviceDefaultRequest(string DeviceId);

public sealed record SessionVolumeRequest(string Key, double Volume);

public sealed record SessionMuteRequest(string Key, bool Muted);

public sealed record BrightnessRequest(int DisplayIndex, int Percent, string StableId = "");

public sealed record TrayPromoteRequest(string Key, bool Promoted);

public sealed record TrayIconRequest(string Id);

public sealed record TrayIconKeyRequest(string Key);

public sealed record LaunchAppRequest(string Id, string ShortcutPath, string Source);

public sealed record WindowsThemeRequest(string Mode);

public sealed record SettingsPageRequest(string Page);

public sealed record UiActivateRequest(string Page);

public sealed record SaveProfileRequest(string Name);

public sealed record ProfileIdRequest(Guid Id);

public sealed record SoundPreviewRequest(string Path);

public sealed record AudioChangedEvent(double Volume, bool Muted);

public sealed record ToastEvent(string Title, string Message);

public sealed record NotificationDto(Guid Id, string Title, string Message, DateTimeOffset CreatedAt);

public sealed record TrayAppWindowDto(
    long Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    string WindowClass);

public sealed record TrayAppHideWindowRequest(TrayAppWindowDto Window);

public sealed record TrayAppRestoreWindowRequest(
    long WindowHandle,
    int ProcessId,
    long ProcessStartTimeUtcTicks);

public sealed record BrightnessChangedEvent(int DisplayIndex, int Brightness);


public sealed record DeviceChangedEvent(string DeviceName);
