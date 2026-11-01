using System.Runtime.InteropServices;

namespace TrayX.Windows.Audio;

internal static class AudioInterop
{
    public const int DeviceStateActive = 0x00000001;
    public const int StorageModeRead = 0;
    public const uint ClsCtxAll = 23;

    public const ushort VtLpwstr = 31;
    public const ushort VtBstr = 8;

    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
    public static readonly Guid IID_IMMNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");

    public static readonly PropertyKey DeviceFriendlyName = new()
    {
        FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        PropertyId = 14
    };

    public static void Check(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    public static TInterface Activate<TInterface>(IMMDevice device, Guid interfaceId)
        where TInterface : class
    {
        var iid = interfaceId;
        Check(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var instance));
        return (TInterface)instance;
    }

    public static void Release(object? instance)
    {
        if (instance is null || !Marshal.IsComObject(instance))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(instance);
        }
        catch (ArgumentException)
        {
        }
    }

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumeratorComObject
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

    [PreserveSig]
    int RegisterEndpointNotificationCallback(IntPtr client);

    [PreserveSig]
    int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComVisible(true)]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMMNotificationClient
{
    [PreserveSig]
    int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

    [PreserveSig]
    int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

    [PreserveSig]
    int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

    [PreserveSig]
    int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(
        ref Guid interfaceId,
        uint classContext,
        IntPtr activationParameters,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance);

    [PreserveSig]
    int OpenPropertyStore(uint accessMode, out IPropertyStore properties);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint propertyCount);

    [PreserveSig]
    int GetAt(uint propertyIndex, out PropertyKey key);

    [PreserveSig]
    int GetValue(ref PropertyKey key, out PropVariant value);

    [PreserveSig]
    int SetValue(ref PropertyKey key, ref PropVariant value);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int UnregisterControlChangeNotify(IntPtr notify);

    [PreserveSig]
    int GetChannelCount(out uint channelCount);

    [PreserveSig]
    int SetMasterVolumeLevel(float level, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float level);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float level);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

    [PreserveSig]
    int GetVolumeStepInfo(out uint step, out uint stepCount);

    [PreserveSig]
    int VolumeStepUp(ref Guid eventContext);

    [PreserveSig]
    int VolumeStepDown(ref Guid eventContext);

    [PreserveSig]
    int QueryHardwareSupport(out uint hardwareSupportMask);

    [PreserveSig]
    int GetVolumeRange(out float minimum, out float maximum, out float increment);
}

[ComImport]
[Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioMeterInformation
{
    [PreserveSig]
    int GetPeakValue(out float peak);
}

[ComVisible(true)]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notificationData);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    [PreserveSig]
    int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl session);

    [PreserveSig]
    int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume volume);

    [PreserveSig]
    int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);

    [PreserveSig]
    int RegisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterSessionNotification(IntPtr notification);

    [PreserveSig]
    int RegisterDuckNotification(string sessionId, IntPtr notification);

    [PreserveSig]
    int UnregisterDuckNotification(IntPtr notification);
}

[ComVisible(true)]
[Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionNotification
{
    [PreserveSig]
    int OnSessionCreated(IAudioSessionControl newSession);
}

[ComVisible(true)]
[Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionEvents
{
    [PreserveSig]
    int OnDisplayNameChanged([MarshalAs(UnmanagedType.LPWStr)] string newDisplayName, ref Guid eventContext);

    [PreserveSig]
    int OnIconPathChanged([MarshalAs(UnmanagedType.LPWStr)] string newIconPath, ref Guid eventContext);

    [PreserveSig]
    int OnSimpleVolumeChanged(float volume, [MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid eventContext);

    [PreserveSig]
    int OnChannelVolumeChanged(uint channelCount, IntPtr newVolumes, uint changedChannel, ref Guid eventContext);

    [PreserveSig]
    int OnGroupingParamChanged(ref Guid newGroupingParam, ref Guid eventContext);

    [PreserveSig]
    int OnStateChanged(AudioSessionState newState);

    [PreserveSig]
    int OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig]
    int GetCount(out int count);

    [PreserveSig]
    int GetSession(int index, out IAudioSessionControl session);
}

[ComImport]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioSessionControl
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    [PreserveSig]
    int GetState(out AudioSessionState state);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);

    [PreserveSig]
    int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);

    [PreserveSig]
    int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);

    [PreserveSig]
    int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);

    [PreserveSig]
    int GetGroupingParam(out Guid groupingId);

    [PreserveSig]
    int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);

    [PreserveSig]
    int RegisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int UnregisterAudioSessionNotification(IntPtr notification);

    [PreserveSig]
    int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

    [PreserveSig]
    int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string identifier);

    [PreserveSig]
    int GetProcessId(out uint processId);

    [PreserveSig]
    int IsSystemSoundsSession();

    [PreserveSig]
    int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    [PreserveSig]
    int SetMasterVolume(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolume(out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

public enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

public enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}

public enum AudioSessionState
{
    Inactive = 0,
    Active = 1,
    Expired = 3
}

public enum AudioSessionDisconnectReason
{
    DeviceRemoval = 0,
    ServerShutdown = 1,
    FormatChanged = 2,
    SessionLogoff = 3,
    SessionDisconnected = 4,
    ExclusiveModeOverride = 5
}

[StructLayout(LayoutKind.Sequential)]
public struct PropertyKey
{
    public Guid FormatId;
    public uint PropertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Pointer;
    public IntPtr Pointer2;
}
