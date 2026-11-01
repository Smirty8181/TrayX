using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Settings;
using TrayX.Contracts;
using TrayX.Windows;
using TrayX.Windows.Audio;

namespace TrayX.Modules.SoundEvents;

public sealed class SoundEventsModule : ITrayXModule
{
    private readonly AudioCenterService _audio;
    private readonly ISettingsStore _settings;
    private readonly IClientEvents _events;
    private readonly ILogger<SoundEventsModule> _logger;

    private CancellationTokenSource? _lifetime;
    private readonly object _deviceChangeSync = new();
    private Task? _deviceChangeTask;
    private int _deviceChangePending;
    private string _deviceId = string.Empty;

    public SoundEventsModule(
        AudioCenterService audio,
        ISettingsStore settings,
        IClientEvents events,
        ILogger<SoundEventsModule> logger)
    {
        _audio = audio;
        _settings = settings;
        _events = events;
        _logger = logger;
    }

    public string Id => "sound-events";

    public string DisplayName => "Звуковые события";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _deviceId = _audio.GetDefaultDevice().Id;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _audio.DevicesChanged += OnAudioDevicesChanged;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is null)
        {
            return;
        }

        _audio.DevicesChanged -= OnAudioDevicesChanged;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            Task? deviceChangeTask;
            lock (_deviceChangeSync)
            {
                deviceChangeTask = _deviceChangeTask;
            }

            if (deviceChangeTask is not null)
            {
                await deviceChangeTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (_deviceChangeTask?.IsCompleted != false)
            {
                _lifetime.Dispose();
                _lifetime = null;
                _deviceChangeTask = null;
            }
        }
    }

    private void OnAudioDevicesChanged(object? sender, EventArgs e)
    {
        Interlocked.Exchange(ref _deviceChangePending, 1);
        lock (_deviceChangeSync)
        {
            if (_deviceChangeTask is null)
            {
                _deviceChangeTask = Task.Run(ProcessDeviceChangesAsync);
            }
        }
    }

    private async Task ProcessDeviceChangesAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _deviceChangePending, 0) != 0)
            {
                var lifetime = _lifetime;
                if (lifetime is null || lifetime.IsCancellationRequested)
                {
                    return;
                }

                var device = _audio.GetDefaultDevice();
                if (string.IsNullOrEmpty(device.Id) ||
                    string.Equals(device.Id, _deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _deviceId = device.Id;
                _logger.LogInformation("Default audio device changed to {Device}", device.Name);

                if (_settings.Current.PlayDeviceSounds)
                {
                    var soundPath = _settings.Current.DeviceSoundPath;
                    if (string.IsNullOrWhiteSpace(soundPath))
                    {
                        if (!MessageBeep(0x00000040))
                        {
                            _logger.LogWarning("Windows could not play the default device-change sound");
                        }
                    }
                    else if (!TryPlaySound(soundPath))
                    {
                        _logger.LogWarning(
                            "Configured device-change sound could not be played: {Path}",
                            soundPath);
                        _ = MessageBeep(0x00000040);
                    }
                }

                _events.Publish(
                    IpcEvents.AudioDeviceChanged,
                    new DeviceChangedEvent(device.Name));
            }
        }
        catch (OperationCanceledException) when (_lifetime?.IsCancellationRequested == true)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not process audio device change");
        }
        finally
        {
            lock (_deviceChangeSync)
            {
                _deviceChangeTask = null;
                if (Volatile.Read(ref _deviceChangePending) != 0 &&
                    _lifetime?.IsCancellationRequested == false)
                {
                    _deviceChangeTask = Task.Run(ProcessDeviceChangesAsync);
                }
            }
        }
    }

    public bool PreviewSound(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? MessageBeep(0x00000040)
            : TryPlaySound(path);
    }

    private static bool TryPlaySound(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            !string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (IsRemotePath(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        return PlaySound(fullPath, IntPtr.Zero, SoundFilename | SoundAsync | SoundNoDefault);
    }

    internal static bool IsRemotePath(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(path);
        return root is not null && GetDriveType(root) == DriveRemote;
    }

    private const uint DriveRemote = 4;
    private const uint SoundAsync = 0x0001;
    private const uint SoundNoDefault = 0x0002;
    private const uint SoundFilename = 0x00020000;

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetDriveType(string rootPathName);
}
