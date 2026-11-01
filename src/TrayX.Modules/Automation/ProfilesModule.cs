using System.Runtime.InteropServices;
using System.Globalization;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Profiles;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Domain.Entities;
using TrayX.Windows;
using TrayX.Windows.Audio;
using TrayX.Windows.Themes;

namespace TrayX.Modules.Automation;

public sealed class ProfilesModule : ITrayXModule
{
    private readonly IProfileStore _store;
    private readonly AudioCenterService _audio;
    private readonly WindowsThemeService _themes;
    private readonly ISettingsStore _settings;
    private readonly IClientEvents _events;
    private readonly ILogger<ProfilesModule> _logger;
    private CancellationTokenSource? _scheduleCancellation;
    private Task? _scheduleTask;
    private DateOnly? _lastScheduledDate;

    public ProfilesModule(
        IProfileStore store,
        AudioCenterService audio,
        WindowsThemeService themes,
        ISettingsStore settings,
        IClientEvents events,
        ILogger<ProfilesModule> logger)
    {
        _store = store;
        _audio = audio;
        _themes = themes;
        _settings = settings;
        _events = events;
        _logger = logger;
    }

    public string Id => "automation";

    public string DisplayName => "Автоматизация";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var profiles = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (_settings.Current.ScheduledProfileId is { } scheduledProfileId &&
            profiles.All(profile => profile.Id != scheduledProfileId))
        {
            _logger.LogWarning(
                "Scheduled profile {ProfileId} no longer exists; disabling its schedule",
                scheduledProfileId);
            var updated = _settings.Current with { ScheduledProfileId = null };
            await _settings.UpdateAsync(
                current => current with { ScheduledProfileId = null },
                cancellationToken).ConfigureAwait(false);
            updated = _settings.Current;
            _events.Publish(IpcEvents.SettingsChanged, updated);
        }

        _logger.LogInformation("Profiles available: {Count}", profiles.Count);
        _scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scheduleTask = MonitorScheduleAsync(_scheduleCancellation.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduleCancellation is null || _scheduleTask is null)
        {
            return;
        }

        try
        {
            await _scheduleCancellation.CancelAsync().ConfigureAwait(false);
            await _scheduleTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_scheduleTask.IsCompleted)
            {
                _scheduleCancellation.Dispose();
                _scheduleCancellation = null;
                _scheduleTask = null;
            }
        }
    }

    public async Task<IReadOnlyList<ProfileDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var profiles = await _store.GetAllAsync(cancellationToken).ConfigureAwait(false);
        return profiles.Select(ToDto).ToList();
    }

    public async Task<ProfileDto> SaveCurrentAsync(string name, CancellationToken cancellationToken = default)
    {
        var state = _audio.GetState();
        var profile = Profile.Create(
            name,
            (int)Math.Round(state.Volume * 100),
            state.Muted,
            _themes.GetMode(),
            TrayXSettings.NormalizeAccent(_settings.Current.Accent));

        await _store.SaveAsync(profile, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Profile {Name} saved", profile.Name);
        return ToDto(profile);
    }

    public async Task<ProfileDto> ApplyAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var profile = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Профиль не найден.");

        var audioApplied = false;
        try
        {
            var audioState = _audio.GetState();
            if (audioState.HasDevice)
            {
                _audio.SetMasterVolume(profile.VolumePercent / 100.0);
                _audio.SetMasterMute(profile.IsMuted);
                audioApplied = true;
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Audio part of profile {Name} was not applied", profile.Name);
        }

        if (!audioApplied)
        {
            _logger.LogWarning(
                "Profile {Name} was only partially applied because audio settings could not be applied",
                profile.Name);
        }

        _themes.SetMode(profile.WindowsTheme);

        await _settings.UpdateAsync(
            current => current with { Accent = TrayXSettings.NormalizeAccent(profile.Accent) },
            cancellationToken).ConfigureAwait(false);
        var updated = _settings.Current;

        var dto = ToDto(profile) with { AudioApplied = audioApplied };
        _events.Publish(IpcEvents.ProfileApplied, dto);
        _events.Publish(IpcEvents.SettingsChanged, updated);
        if (audioApplied)
        {
            _events.Publish(
                IpcEvents.AudioChanged,
                new AudioChangedEvent(profile.VolumePercent / 100.0, profile.IsMuted));
        }

        _logger.LogInformation(
            "Profile {Name} applied (audio={AudioApplied}, theme={Theme})",
            profile.Name,
            audioApplied,
            profile.WindowsTheme);

        return dto;
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (_settings.Current.ScheduledProfileId == id)
        {
            await _settings.UpdateAsync(
                current => current with { ScheduledProfileId = null },
                cancellationToken).ConfigureAwait(false);
            var updated = _settings.Current;
            _events.Publish(IpcEvents.SettingsChanged, updated);
        }

        _logger.LogInformation("Profile {Id} deleted", id);
    }

    private async Task MonitorScheduleAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        do
        {
            var now = DateTime.Now;
            var settings = _settings.Current;
            var today = DateOnly.FromDateTime(now);
            if (settings.ScheduledProfileId is { } profileId &&
                string.Equals(
                    settings.ScheduledProfileTime,
                    now.ToString("HH:mm", CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) &&
                _lastScheduledDate != today)
            {
                _lastScheduledDate = today;
                try
                {
                    await ApplyAsync(profileId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "Scheduled profile {ProfileId} could not be applied",
                        profileId);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private static ProfileDto ToDto(Profile profile) => new(
        profile.Id,
        profile.Name,
        profile.VolumePercent,
        profile.IsMuted,
        profile.WindowsTheme,
        profile.Accent,
        profile.CreatedAt);
}
