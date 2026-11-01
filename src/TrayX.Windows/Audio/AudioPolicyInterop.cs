using System.Runtime.InteropServices;

namespace TrayX.Windows.Audio;

internal static class AudioPolicyInterop
{
    private static readonly Guid ClsidPolicyConfigClient = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    public static bool SetDefaultEndpoint(string deviceId, ERole role)
    {
        Type? type;
        object? instance;
        try
        {
            type = Type.GetTypeFromCLSID(ClsidPolicyConfigClient, throwOnError: true);
            instance = type is null ? null : Activator.CreateInstance(type);
        }
        catch (COMException exception) when (exception.HResult == unchecked((int)0x80040154))
        {
            return false;
        }

        if (instance is null)
        {
            return false;
        }

        try
        {
            if (instance is IPolicyConfig policyConfig)
            {
                if (TrySetDefaultEndpoint(policyConfig.SetDefaultEndpoint(deviceId, role)))
                {
                    return true;
                }
            }

            if (instance is IPolicyConfigVista vista)
            {
                return TrySetDefaultEndpoint(vista.SetDefaultEndpoint(deviceId, role));
            }

            return false;
        }
        finally
        {
            AudioInterop.Release(instance);
        }
    }

    private static bool TrySetDefaultEndpoint(int result)
    {
        if (result == unchecked((int)0x80004001) ||
            result == unchecked((int)0x80004002))
        {
            return false;
        }

        AudioInterop.Check(result);
        return true;
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        int Placeholder0();
        int Placeholder1();
        int Placeholder2();
        int Placeholder3();
        int Placeholder4();
        int Placeholder5();
        int Placeholder6();
        int Placeholder7();
        int Placeholder8();
        int Placeholder9();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }

    [ComImport]
    [Guid("568B9108-44BF-40B4-9006-86AFE5B5A620")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfigVista
    {
        int Placeholder0();
        int Placeholder1();
        int Placeholder2();
        int Placeholder3();
        int Placeholder4();
        int Placeholder5();
        int Placeholder6();
        int Placeholder7();
        int Placeholder8();
        int Placeholder9();

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }
}
