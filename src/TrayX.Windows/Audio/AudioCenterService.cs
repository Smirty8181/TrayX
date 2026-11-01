using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TrayX.Contracts;

namespace TrayX.Windows.Audio;

public sealed record AudioDeviceNotificationEventArgs(string DeviceId, string DeviceName, string Change);

public sealed record AudioDefaultDevice(string Id, string Name);

public sealed class AudioCenterService
{
    private readonly object _sync = new();
    private readonly ConcurrentDictionary<string, string?> _descriptions = new(StringComparer.OrdinalIgnoreCase);
    private IMMDeviceEnumerator? _notificationEnumerator;
    private DeviceNotificationClient? _notificationClient;
    private IntPtr _notificationPointer;
    private SessionNotificationTracker? _sessionTracker;
    private EndpointVolumeTracker? _endpointVolumeTracker;

    public event EventHandler? DevicesChanged;

    public event EventHandler? SessionsChanged;

    public event EventHandler<AudioDeviceNotificationEventArgs>? DeviceNotification;

    public void StartDeviceNotifications()
    {
        lock (_sync)
        {
            if (_notificationEnumerator is not null)
            {
                return;
            }

            var enumerator = CreateEnumerator();
            var client = new DeviceNotificationClient(
                QueueDevicesChanged,
                QueueDeviceNotification);
            var pointer = Marshal.GetComInterfaceForObject(client, typeof(IMMNotificationClient));
            try
            {
                AudioInterop.Check(enumerator.RegisterEndpointNotificationCallback(pointer));
                _notificationEnumerator = enumerator;
                _notificationClient = client;
                _notificationPointer = pointer;
                _sessionTracker = new SessionNotificationTracker(
                    () => SessionsChanged?.Invoke(this, EventArgs.Empty));
                _endpointVolumeTracker = new EndpointVolumeTracker(
                    () => DevicesChanged?.Invoke(this, EventArgs.Empty));
            }
            catch
            {
                Marshal.Release(pointer);
                AudioInterop.Release(enumerator);
                throw;
            }
        }
    }

    private void QueueDevicesChanged()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Audio device change subscriber failed: {exception}");
            }
        });
    }

    public void StopDeviceNotifications()
    {
        lock (_sync)
        {
            if (_notificationEnumerator is null)
            {
                return;
            }

            try
            {
                AudioInterop.Check(
                    _notificationEnumerator.UnregisterEndpointNotificationCallback(_notificationPointer));
            }
            finally
            {
                _sessionTracker?.Dispose();
                _sessionTracker = null;
                _endpointVolumeTracker?.Dispose();
                _endpointVolumeTracker = null;
                Marshal.Release(_notificationPointer);
                _notificationPointer = IntPtr.Zero;
                _notificationClient = null;
                AudioInterop.Release(_notificationEnumerator);
                _notificationEnumerator = null;
            }
        }
    }

    public bool SetDefaultDevice(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                enumerator = CreateEnumerator();
                AudioInterop.Check(enumerator.GetDevice(deviceId, out device));
                AudioInterop.Check(device.GetState(out var state));
                if ((state & AudioInterop.DeviceStateActive) == 0)
                {
                    throw new InvalidOperationException("Выбранное аудиоустройство сейчас неактивно.");
                }

                AudioInterop.Check(device.GetId(out var verifiedId));
                if (!AudioPolicyInterop.SetDefaultEndpoint(verifiedId, ERole.Console) ||
                    !AudioPolicyInterop.SetDefaultEndpoint(verifiedId, ERole.Multimedia) ||
                    !AudioPolicyInterop.SetDefaultEndpoint(verifiedId, ERole.Communications))
                {
                    return false;
                }

                return true;
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                throw new InvalidOperationException(DescribeFailure(exception), exception);
            }
            finally
            {
                AudioInterop.Release(device);
                AudioInterop.Release(enumerator);
            }
        }
    }

    public AudioStateDto GetState()
    {
        lock (_sync)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? defaultRender = null;
            IMMDevice? defaultCapture = null;
            IAudioEndpointVolume? endpoint = null;

            try
            {
                enumerator = CreateEnumerator();
                var defaultRenderId = string.Empty;
                var defaultCaptureId = string.Empty;

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out defaultRender) >= 0)
                {
                    defaultRender.GetId(out defaultRenderId);
                }

                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out defaultCapture) >= 0)
                {
                    defaultCapture.GetId(out defaultCaptureId);
                }

                var outputs = ReadDevices(enumerator, EDataFlow.Render, defaultRenderId);
                var inputs = ReadDevices(enumerator, EDataFlow.Capture, defaultCaptureId);
                _endpointVolumeTracker?.SetDevices(enumerator, outputs.Concat(inputs).Select(device => device.Id));

                var volume = 0.0;
                var muted = false;
                var deviceName = string.Empty;
                var deviceId = defaultRenderId;
                var sessions = new List<AudioSessionDto>();

                if (defaultRender is not null)
                {
                    try
                    {
                        _sessionTracker?.SetDevice(defaultRender);
                    }
                    catch (Exception exception) when (IsAudioFailure(exception))
                    {
                        Debug.WriteLine($"Audio session notifications unavailable: {exception}");
                    }

                    deviceName = outputs.FirstOrDefault(device => device.IsDefault)?.Name ?? "Устройство вывода";
                    endpoint = AudioInterop.Activate<IAudioEndpointVolume>(
                        defaultRender,
                        AudioInterop.IID_IAudioEndpointVolume);

                    if (endpoint.GetMasterVolumeLevelScalar(out var scalar) >= 0)
                    {
                        volume = Math.Round(scalar, 3);
                    }

                    if (endpoint.GetMute(out muted) < 0)
                    {
                        muted = false;
                    }

                    outputs = outputs
                        .Select(device => device.IsDefault
                            ? device with { Volume = volume, IsMuted = muted, CanControl = true }
                            : device)
                        .ToList();

                    sessions = ReadSessions(defaultRender);
                }

                var error = defaultRender is null ? "Устройство воспроизведения не найдено." : null;

                return new AudioStateDto(
                    defaultRender is not null,
                    deviceId,
                    deviceName,
                    volume,
                    muted,
                    outputs,
                    inputs,
                    sessions,
                    error);
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                return new AudioStateDto(
                    false,
                    string.Empty,
                    string.Empty,
                    0,
                    false,
                    Array.Empty<AudioDeviceDto>(),
                    Array.Empty<AudioDeviceDto>(),
                    Array.Empty<AudioSessionDto>(),
                    DescribeFailure(exception));
            }
            finally
            {
                AudioInterop.Release(endpoint);
                AudioInterop.Release(defaultRender);
                AudioInterop.Release(defaultCapture);
                AudioInterop.Release(enumerator);
            }
        }
    }

    public AudioLevelDto GetMasterLevel()
    {
        lock (_sync)
        {
            try
            {
                return WithDefaultEndpoint(ReadLevel);
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                return new AudioLevelDto(0, false);
            }
        }
    }

    public AudioPeakDto? GetDefaultInputPeak()
    {
        lock (_sync)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioMeterInformation? meter = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Capture, ERole.Console, out device) < 0 ||
                    device is null)
                {
                    return null;
                }

                meter = AudioInterop.Activate<IAudioMeterInformation>(
                    device,
                    AudioInterop.IID_IAudioMeterInformation);
                if (meter.GetPeakValue(out var peak) < 0)
                {
                    return null;
                }

                return new AudioPeakDto(Math.Clamp(peak, 0, 1));
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                return null;
            }
            finally
            {
                AudioInterop.Release(meter);
                AudioInterop.Release(device);
                AudioInterop.Release(enumerator);
            }
        }
    }

    public string GetDefaultDeviceName()
        => GetDefaultDevice().Name;

    public AudioDefaultDevice GetDefaultDevice()
    {
        lock (_sync)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device) < 0 ||
                    device is null)
                {
                    return new AudioDefaultDevice(string.Empty, string.Empty);
                }

                AudioInterop.Check(device.GetId(out var id));
                return new AudioDefaultDevice(id, ReadFriendlyName(device) ?? string.Empty);
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                return new AudioDefaultDevice(string.Empty, string.Empty);
            }
            finally
            {
                AudioInterop.Release(device);
                AudioInterop.Release(enumerator);
            }
        }
    }

    public AudioLevelDto SetMasterVolume(double volume)
    {
        var clamped = (float)Math.Clamp(volume, 0, 1);
        lock (_sync)
        {
            return WithDefaultEndpoint(endpoint =>
            {
                var context = Guid.Empty;
                AudioInterop.Check(endpoint.SetMasterVolumeLevelScalar(clamped, ref context));
                return ReadLevel(endpoint);
            });
        }
    }

    public AudioLevelDto SetMasterMute(bool muted)
    {
        lock (_sync)
        {
            return WithDefaultEndpoint(endpoint =>
            {
                var context = Guid.Empty;
                AudioInterop.Check(endpoint.SetMute(muted, ref context));
                return ReadLevel(endpoint);
            });
        }
    }

    public AudioLevelDto SetDeviceVolume(string deviceId, double volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var clamped = (float)Math.Clamp(volume, 0, 1);
        lock (_sync)
        {
            return WithDeviceEndpoint(deviceId, endpoint =>
            {
                var context = Guid.Empty;
                AudioInterop.Check(endpoint.SetMasterVolumeLevelScalar(clamped, ref context));
                return ReadLevel(endpoint);
            });
        }
    }

    public AudioLevelDto SetDeviceMute(string deviceId, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            return WithDeviceEndpoint(deviceId, endpoint =>
            {
                var context = Guid.Empty;
                AudioInterop.Check(endpoint.SetMute(muted, ref context));
                return ReadLevel(endpoint);
            });
        }
    }

    public AudioLevelDto SetSessionVolume(string key, double volume)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var clamped = (float)Math.Clamp(volume, 0, 1);

        lock (_sync)
        {
            return ApplyToSessions(key, (simple, context) =>
            {
                AudioInterop.Check(simple.SetMasterVolume(clamped, ref context));
            });
        }
    }

    public AudioLevelDto SetSessionMute(string key, bool muted)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            return ApplyToSessions(key, (simple, context) =>
            {
                AudioInterop.Check(simple.SetMute(muted, ref context));
            });
        }
    }

    private AudioLevelDto WithDefaultEndpoint(Func<IAudioEndpointVolume, AudioLevelDto> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;

        try
        {
            enumerator = CreateEnumerator();
            AudioInterop.Check(
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device));
            endpoint = AudioInterop.Activate<IAudioEndpointVolume>(
                device,
                AudioInterop.IID_IAudioEndpointVolume);
            return action(endpoint);
        }
        catch (Exception exception) when (IsAudioFailure(exception))
        {
            throw new InvalidOperationException(DescribeFailure(exception), exception);
        }
        finally
        {
            AudioInterop.Release(endpoint);
            AudioInterop.Release(device);
            AudioInterop.Release(enumerator);
        }
    }

    private AudioLevelDto WithDeviceEndpoint(
        string deviceId,
        Func<IAudioEndpointVolume, AudioLevelDto> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioEndpointVolume? endpoint = null;

        try
        {
            enumerator = CreateEnumerator();
            AudioInterop.Check(enumerator.GetDevice(deviceId, out device));
            endpoint = AudioInterop.Activate<IAudioEndpointVolume>(
                device,
                AudioInterop.IID_IAudioEndpointVolume);
            return action(endpoint);
        }
        catch (Exception exception) when (IsAudioFailure(exception))
        {
            throw new InvalidOperationException(DescribeFailure(exception), exception);
        }
        finally
        {
            AudioInterop.Release(endpoint);
            AudioInterop.Release(device);
            AudioInterop.Release(enumerator);
        }
    }

    private static AudioLevelDto ReadLevel(IAudioEndpointVolume endpoint)
    {
        var volume = 0.0;
        var muted = false;
        if (endpoint.GetMasterVolumeLevelScalar(out var scalar) >= 0)
        {
            volume = Math.Round(scalar, 3);
        }

        if (endpoint.GetMute(out muted) < 0)
        {
            muted = false;
        }

        return new AudioLevelDto(volume, muted);
    }

    private AudioLevelDto ApplyToSessions(string key, Action<ISimpleAudioVolume, Guid> apply)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;

        try
        {
            enumerator = CreateEnumerator();
            AudioInterop.Check(
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device));
            manager = AudioInterop.Activate<IAudioSessionManager2>(
                device,
                AudioInterop.IID_IAudioSessionManager2);
            AudioInterop.Check(manager.GetSessionEnumerator(out sessions));
            AudioInterop.Check(sessions.GetCount(out var count));

            var matched = 0;
            var resultVolume = 0.0;
            var resultMuted = false;

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) < 0 || control is null)
                    {
                        continue;
                    }

                    if (control is not IAudioSessionControl2 control2 ||
                        control2.GetState(out var state) < 0 ||
                        state == AudioSessionState.Expired)
                    {
                        continue;
                    }

                    if (SessionKey(control2) != key)
                    {
                        continue;
                    }

                    if (control is not ISimpleAudioVolume simple)
                    {
                        continue;
                    }

                    var context = Guid.Empty;
                    apply(simple, context);
                    matched++;

                    if (simple.GetMasterVolume(out var volume) >= 0)
                    {
                        resultVolume = Math.Round(volume, 3);
                    }

                    if (simple.GetMute(out var muted) >= 0)
                    {
                        resultMuted = muted;
                    }
                }
                finally
                {
                    AudioInterop.Release(control);
                }
            }

            if (matched == 0)
            {
                throw new InvalidOperationException(
                    "Аудиосессия уже завершилась. Обновите список приложений.");
            }

            return new AudioLevelDto(resultVolume, resultMuted);
        }
        catch (Exception exception) when (IsAudioFailure(exception))
        {
            throw new InvalidOperationException(DescribeFailure(exception), exception);
        }
        finally
        {
            AudioInterop.Release(sessions);
            AudioInterop.Release(manager);
            AudioInterop.Release(device);
            AudioInterop.Release(enumerator);
        }
    }

    private static List<AudioDeviceDto> ReadDevices(
        IMMDeviceEnumerator enumerator,
        EDataFlow flow,
        string defaultId)
    {
        var devices = new List<AudioDeviceDto>();
        IMMDeviceCollection? collection = null;

        try
        {
            AudioInterop.Check(enumerator.EnumAudioEndpoints(flow, AudioInterop.DeviceStateActive, out collection));
            AudioInterop.Check(collection.GetCount(out var count));

            for (var index = 0u; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(index, out device) < 0 || device is null)
                    {
                        continue;
                    }

                    if (device.GetId(out var id) < 0)
                    {
                        continue;
                    }

                    var volume = ReadEndpointLevel(device);
                    devices.Add(new AudioDeviceDto(
                        id,
                        ReadFriendlyName(device) ??
                        (flow == EDataFlow.Render ? "Устройство вывода" : "Устройство ввода"),
                        flow == EDataFlow.Render ? "render" : "capture",
                        string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase),
                        volume?.Muted ?? false,
                        volume?.Volume,
                        volume is not null));
                }
                finally
                {
                    AudioInterop.Release(device);
                }
            }
        }
        finally
        {
            AudioInterop.Release(collection);
        }

        return devices;
    }

    private static AudioLevelDto? ReadEndpointLevel(IMMDevice device)
    {
        IAudioEndpointVolume? endpoint = null;
        try
        {
            endpoint = AudioInterop.Activate<IAudioEndpointVolume>(
                device,
                AudioInterop.IID_IAudioEndpointVolume);
            return ReadLevel(endpoint);
        }
        catch (Exception exception) when (IsAudioFailure(exception))
        {
            return null;
        }
        finally
        {
            AudioInterop.Release(endpoint);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(AudioInterop.StorageModeRead, out store) < 0 || store is null)
            {
                return null;
            }

            var key = AudioInterop.DeviceFriendlyName;
            if (store.GetValue(ref key, out var value) < 0)
            {
                return null;
            }

            try
            {
                if (value.ValueType == AudioInterop.VtLpwstr)
                {
                    return Marshal.PtrToStringUni(value.Pointer);
                }

                if (value.ValueType == AudioInterop.VtBstr)
                {
                    return Marshal.PtrToStringBSTR(value.Pointer);
                }

                return null;
            }
            finally
            {
                _ = AudioInterop.PropVariantClear(ref value);
            }
        }
        finally
        {
            AudioInterop.Release(store);
        }
    }

    private void QueueDeviceNotification(string deviceId, string change)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            var name = deviceId;
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            try
            {
                enumerator = CreateEnumerator();
                if (enumerator.GetDevice(deviceId, out device) >= 0 && device is not null)
                {
                    name = ReadFriendlyName(device) ?? deviceId;
                }
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                Debug.WriteLine($"Audio endpoint name lookup failed: {exception}");
            }
            finally
            {
                AudioInterop.Release(device);
                AudioInterop.Release(enumerator);
            }

            try
            {
                DeviceNotification?.Invoke(
                    this,
                    new AudioDeviceNotificationEventArgs(deviceId, name, change));
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Audio device notification subscriber failed: {exception}");
            }
        });
    }

    private List<AudioSessionDto> ReadSessions(IMMDevice device)
    {
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? enumerator = null;
        var raw = new List<RawSession>();

        try
        {
            manager = AudioInterop.Activate<IAudioSessionManager2>(
                device,
                AudioInterop.IID_IAudioSessionManager2);
            AudioInterop.Check(manager.GetSessionEnumerator(out enumerator));
            AudioInterop.Check(enumerator.GetCount(out var count));

            for (var index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (enumerator.GetSession(index, out control) < 0 || control is null)
                    {
                        continue;
                    }

                    if (control is not IAudioSessionControl2 control2 ||
                        control2.GetState(out var state) < 0 ||
                        state == AudioSessionState.Expired)
                    {
                        continue;
                    }

                    var systemSounds = control2.IsSystemSoundsSession() == 0;
                    var pid = 0u;
                    _ = control2.GetProcessId(out pid);

                    var volume = 1.0;
                    var muted = false;
                    if (control is ISimpleAudioVolume simple)
                    {
                        if (simple.GetMasterVolume(out var scalar) >= 0)
                        {
                            volume = Math.Round(scalar, 3);
                        }

                        if (simple.GetMute(out muted) < 0)
                        {
                            muted = false;
                        }
                    }

                    var process = systemSounds ? null : GetProcessInfo((int)pid);
                    var name = systemSounds
                        ? "Системные звуки"
                        : process?.Description ?? process?.Name ?? "Приложение";
                    var key = systemSounds
                        ? "system"
                        : "app:" + (process?.Name ?? $"pid:{pid}").ToLowerInvariant();

                    raw.Add(new RawSession(
                        key,
                        name,
                        (int)pid,
                        process?.Path,
                        volume,
                        muted,
                        state == AudioSessionState.Active));
                }
                finally
                {
                    AudioInterop.Release(control);
                }
            }
        }
        finally
        {
            AudioInterop.Release(enumerator);
            AudioInterop.Release(manager);
        }

        return raw
            .GroupBy(session => session.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var first = group.First();
                return new AudioSessionDto(
                    group.Key,
                    first.DisplayName,
                    first.ProcessId,
                    first.ProcessPath,
                    Math.Round(group.Average(item => item.Volume), 3),
                    group.All(item => item.Muted),
                    group.Any(item => item.IsActive) ? "active" : "inactive",
                    group.Key == "system");
            })
            .OrderBy(session => session.IsSystemSounds)
            .ThenBy(session => session.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string SessionKey(IAudioSessionControl2 control)
    {
        _ = control.GetProcessId(out var pid);
        if (control.IsSystemSoundsSession() == 0)
        {
            return "system";
        }

        var process = GetProcessInfo((int)pid);
        return "app:" + (process?.Name ?? $"pid:{pid}").ToLowerInvariant();
    }

    private ProcessInfo? GetProcessInfo(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var name = process.ProcessName;

            string? path = null;
            try
            {
                path = process.MainModule?.FileName;
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or
                InvalidOperationException or
                NotSupportedException)
            {
            }

            string? description = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                description = _descriptions.GetOrAdd(path, ReadDescription);
            }

            return new ProcessInfo(name, description, path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? ReadDescription(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var description = info.FileDescription?.Trim();
            return string.IsNullOrWhiteSpace(description) ? null : description;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var type = Type.GetTypeFromCLSID(AudioInterop.ClsidMMDeviceEnumerator, throwOnError: true)
            ?? throw new InvalidOperationException("Windows Audio недоступен.");
        return (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    private static bool IsAudioFailure(Exception exception) =>
        exception is COMException or
            InvalidCastException or
            InvalidOperationException or
            UnauthorizedAccessException;

    private static string DescribeFailure(Exception exception) =>
        exception is COMException com
            ? $"Ошибка Windows Audio (0x{com.HResult:X8}): {com.Message}"
            : exception.Message;

    private sealed record ProcessInfo(string Name, string? Description, string? Path);

    private sealed record RawSession(
        string Key,
        string DisplayName,
        int ProcessId,
        string? ProcessPath,
        double Volume,
        bool Muted,
        bool IsActive);

    private sealed class EndpointVolumeTracker(Action changed) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, EndpointRegistration> _endpoints =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _disposed;
        private int _notificationPending;

        public void SetDevices(IMMDeviceEnumerator enumerator, IEnumerable<string> deviceIds)
        {
            var activeIds = deviceIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                foreach (var staleId in _endpoints.Keys.Where(id => !activeIds.Contains(id)).ToArray())
                {
                    Remove(staleId);
                }

                foreach (var deviceId in activeIds)
                {
                    if (_endpoints.ContainsKey(deviceId))
                    {
                        continue;
                    }

                    IMMDevice? device = null;
                    IAudioEndpointVolume? endpoint = null;
                    IntPtr callbackPointer = IntPtr.Zero;
                    try
                    {
                        AudioInterop.Check(enumerator.GetDevice(deviceId, out device));
                        endpoint = AudioInterop.Activate<IAudioEndpointVolume>(
                            device,
                            AudioInterop.IID_IAudioEndpointVolume);
                        var callback = new EndpointVolumeEventsClient(this);
                        callbackPointer = Marshal.GetComInterfaceForObject(
                            callback,
                            typeof(IAudioEndpointVolumeCallback));
                        AudioInterop.Check(endpoint.RegisterControlChangeNotify(callbackPointer));
                        _endpoints.Add(
                            deviceId,
                            new EndpointRegistration(endpoint, callback, callbackPointer));
                        endpoint = null;
                        callbackPointer = IntPtr.Zero;
                    }
                    catch (Exception exception) when (IsAudioFailure(exception))
                    {
                        Debug.WriteLine($"Audio endpoint volume notifications unavailable for {deviceId}: {exception}");
                    }
                    finally
                    {
                        if (callbackPointer != IntPtr.Zero)
                        {
                            Marshal.Release(callbackPointer);
                        }

                        AudioInterop.Release(endpoint);
                        AudioInterop.Release(device);
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                foreach (var deviceId in _endpoints.Keys.ToArray())
                {
                    Remove(deviceId);
                }
            }
        }

        private void NotifyChanged()
        {
            if (Interlocked.Exchange(ref _notificationPending, 1) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    changed();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Audio endpoint volume notification failed: {exception}");
                }
                finally
                {
                    Volatile.Write(ref _notificationPending, 0);
                }
            });
        }

        private void Remove(string deviceId)
        {
            if (!_endpoints.Remove(deviceId, out var registration))
            {
                return;
            }

            try
            {
                AudioInterop.Check(registration.Endpoint.UnregisterControlChangeNotify(registration.CallbackPointer));
            }
            catch (Exception exception) when (IsAudioFailure(exception))
            {
                Debug.WriteLine($"Audio endpoint callback unregistration failed: {exception}");
            }
            finally
            {
                Marshal.Release(registration.CallbackPointer);
                AudioInterop.Release(registration.Endpoint);
            }
        }

        private sealed record EndpointRegistration(
            IAudioEndpointVolume Endpoint,
            EndpointVolumeEventsClient Callback,
            IntPtr CallbackPointer);

        private sealed class EndpointVolumeEventsClient(EndpointVolumeTracker owner) : IAudioEndpointVolumeCallback
        {
            public int OnNotify(IntPtr notificationData)
            {
                owner.NotifyChanged();
                return 0;
            }
        }
    }

    private sealed class SessionNotificationTracker(Action changed) : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, SessionRegistration> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private IAudioSessionManager2? _manager;
        private SessionNotificationClient? _client;
        private IntPtr _clientPointer;
        private string _deviceId = string.Empty;
        private bool _disposed;
        private int _notificationPending;

        public void SetDevice(IMMDevice device)
        {
            AudioInterop.Check(device.GetId(out var deviceId));
            lock (_gate)
            {
                if (_disposed || string.Equals(_deviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                Reset();
                _deviceId = deviceId;
                _manager = AudioInterop.Activate<IAudioSessionManager2>(
                    device,
                    AudioInterop.IID_IAudioSessionManager2);

                _client = new SessionNotificationClient(this);
                _clientPointer = Marshal.GetComInterfaceForObject(_client, typeof(IAudioSessionNotification));
                try
                {
                    AudioInterop.Check(_manager.RegisterSessionNotification(_clientPointer));
                    AttachExistingSessions();
                }
                catch
                {
                    Reset();
                    throw;
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                Reset();
            }
        }

        private void AttachExistingSessions()
        {
            IAudioSessionEnumerator? enumerator = null;
            try
            {
                AudioInterop.Check(_manager!.GetSessionEnumerator(out enumerator));
                AudioInterop.Check(enumerator.GetCount(out var count));
                for (var index = 0; index < count; index++)
                {
                    IAudioSessionControl? session = null;
                    try
                    {
                        if (enumerator.GetSession(index, out session) >= 0 && session is not null)
                        {
                            AttachSession(session);
                            session = null;
                        }
                    }
                    finally
                    {
                        AudioInterop.Release(session);
                    }
                }
            }
            finally
            {
                AudioInterop.Release(enumerator);
            }
        }

        private void AttachSession(IAudioSessionControl session)
        {
            lock (_gate)
            {
                if (_disposed || session is not IAudioSessionControl2 session2 ||
                    session2.GetSessionInstanceIdentifier(out var instanceId) < 0 ||
                    string.IsNullOrWhiteSpace(instanceId) ||
                    _sessions.ContainsKey(instanceId))
                {
                    AudioInterop.Release(session);
                    return;
                }

                var client = new AudioSessionEventsClient(this, instanceId);
                var pointer = Marshal.GetComInterfaceForObject(client, typeof(IAudioSessionEvents));
                try
                {
                    AudioInterop.Check(session.RegisterAudioSessionNotification(pointer));
                    _sessions.Add(instanceId, new SessionRegistration(session, client, pointer));
                    session = null!;
                }
                catch
                {
                    Marshal.Release(pointer);
                    throw;
                }
                finally
                {
                    AudioInterop.Release(session);
                }
            }
        }

        private void NotifyChanged()
        {
            if (Interlocked.Exchange(ref _notificationPending, 1) != 0)
            {
                return;
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    changed();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"Audio session notification failed: {exception}");
                }
                finally
                {
                    Volatile.Write(ref _notificationPending, 0);
                }
            });
        }

        private void ScheduleRemove(string instanceId)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_gate)
                {
                    if (!_sessions.Remove(instanceId, out var registration))
                    {
                        return;
                    }

                    try
                    {
                        AudioInterop.Check(
                            ((IAudioSessionControl2)registration.Session)
                            .UnregisterAudioSessionNotification(registration.CallbackPointer));
                    }
                    catch (Exception exception) when (IsAudioFailure(exception))
                    {
                        Debug.WriteLine($"Audio session callback unregistration failed: {exception}");
                    }
                    finally
                    {
                        Marshal.Release(registration.CallbackPointer);
                        AudioInterop.Release(registration.Session);
                    }
                }
            });
        }

        private void Reset()
        {
            foreach (var registration in _sessions.Values)
            {
                try
                {
                    AudioInterop.Check(
                        ((IAudioSessionControl2)registration.Session)
                        .UnregisterAudioSessionNotification(registration.CallbackPointer));
                }
                catch (Exception exception) when (IsAudioFailure(exception))
                {
                    Debug.WriteLine($"Audio session callback unregistration failed: {exception}");
                }
                finally
                {
                    Marshal.Release(registration.CallbackPointer);
                    AudioInterop.Release(registration.Session);
                }
            }

            _sessions.Clear();
            if (_manager is not null && _clientPointer != IntPtr.Zero)
            {
                try
                {
                    AudioInterop.Check(_manager.UnregisterSessionNotification(_clientPointer));
                }
                catch (Exception exception) when (IsAudioFailure(exception))
                {
                    Debug.WriteLine($"Audio session notification unregistration failed: {exception}");
                }
                finally
                {
                    Marshal.Release(_clientPointer);
                    _clientPointer = IntPtr.Zero;
                }
            }

            _client = null;
            AudioInterop.Release(_manager);
            _manager = null;
            _deviceId = string.Empty;
        }

        private sealed record SessionRegistration(
            IAudioSessionControl Session,
            AudioSessionEventsClient Callback,
            IntPtr CallbackPointer);

        private sealed class SessionNotificationClient(SessionNotificationTracker owner) : IAudioSessionNotification
        {
            public int OnSessionCreated(IAudioSessionControl newSession)
            {
                try
                {
                    owner.AttachSession(newSession);
                    owner.NotifyChanged();
                }
                catch (Exception exception)
                {
                    Debug.WriteLine($"New audio session could not be observed: {exception}");
                }

                return 0;
            }
        }

        private sealed class AudioSessionEventsClient(
            SessionNotificationTracker owner,
            string instanceId) : IAudioSessionEvents
        {
            public int OnDisplayNameChanged(string newDisplayName, ref Guid eventContext)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnIconPathChanged(string newIconPath, ref Guid eventContext)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnSimpleVolumeChanged(float volume, bool muted, ref Guid eventContext)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint changedChannel, ref Guid eventContext)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnStateChanged(AudioSessionState newState)
            {
                owner.NotifyChanged();
                return 0;
            }

            public int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
            {
                owner.NotifyChanged();
                owner.ScheduleRemove(instanceId);
                return 0;
            }
        }
    }

    private sealed class DeviceNotificationClient(
        Action changed,
        Action<string, string> notify) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState)
        {
            changed();
            return 0;
        }

        public int OnDeviceAdded(string deviceId)
        {
            changed();
            notify(deviceId, "connected");
            return 0;
        }

        public int OnDeviceRemoved(string deviceId)
        {
            changed();
            notify(deviceId, "disconnected");
            return 0;
        }

        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            changed();
            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, ref PropertyKey key)
        {
            changed();
            return 0;
        }
    }
}
