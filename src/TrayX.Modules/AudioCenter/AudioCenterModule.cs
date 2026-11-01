using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Audio;

namespace TrayX.Modules.AudioCenter;

public sealed class AudioCenterModule : ITrayXModule
{
    private readonly ILogger<AudioCenterModule> _logger;
    private readonly IClientEvents _events;
    private readonly ISettingsStore _settings;

    public AudioCenterModule(
        AudioCenterService service,
        IClientEvents events,
        ISettingsStore settings,
        ILogger<AudioCenterModule> logger)
    {
        Service = service;
        _events = events;
        _settings = settings;
        _logger = logger;
    }

    public AudioCenterService Service { get; }

    public string Id => "audio";

    public string DisplayName => "Аудиоцентр";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Service.DevicesChanged += OnDevicesChanged;
        Service.SessionsChanged += OnSessionsChanged;
        Service.DeviceNotification += OnDeviceNotification;
        try
        {
            Service.StartDeviceNotifications();

            var state = await Task.Run(Service.GetState, cancellationToken).ConfigureAwait(false);
            if (state.HasDevice)
            {
                _logger.LogInformation(
                    "Audio endpoint {Device}, {Sessions} active sessions",
                    state.DeviceName,
                    state.Sessions.Count);
            }
            else
            {
                _logger.LogWarning("Audio subsystem unavailable: {Error}", state.Error);
            }
        }
        catch (Exception startupException)
        {
            Service.DevicesChanged -= OnDevicesChanged;
            Service.SessionsChanged -= OnSessionsChanged;
            Service.DeviceNotification -= OnDeviceNotification;
            try
            {
                Service.StopDeviceNotifications();
            }
            catch (Exception cleanupException)
            {
                _logger.LogError(cleanupException, "Audio device notifications failed to roll back");
                throw new AggregateException(
                    "Audio Center startup failed and callback cleanup was incomplete.",
                    startupException,
                    cleanupException);
            }

            _logger.LogError(startupException, "Audio Center failed to start");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Service.DevicesChanged -= OnDevicesChanged;
        Service.SessionsChanged -= OnSessionsChanged;
        Service.DeviceNotification -= OnDeviceNotification;
        Service.StopDeviceNotifications();
        return Task.CompletedTask;
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        _events.Publish(IpcEvents.AudioDevicesChanged);
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        _events.Publish(IpcEvents.AudioSessionsChanged);
    }

    private void OnDeviceNotification(object? sender, AudioDeviceNotificationEventArgs e)
    {
        if (!_settings.Current.ShowTrayNotifications)
        {
            return;
        }

        var connected = e.Change == "connected";
        var title = _settings.Current.Language == "en"
            ? connected ? "Audio device connected" : "Audio device disconnected"
            : connected ? "Аудиоустройство подключено" : "Аудиоустройство отключено";
        _events.Publish(
            IpcEvents.Toast,
            new ToastEvent(title, e.DeviceName));
    }
}
