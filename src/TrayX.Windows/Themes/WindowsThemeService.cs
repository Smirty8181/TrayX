using Microsoft.Win32;

namespace TrayX.Windows.Themes;

public sealed class WindowsThemeService
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public string GetMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, writable: false);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int light && light == 0 ? "dark" : "light";
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return "dark";
        }
    }

    public void SetMode(string mode)
    {
        var light = mode == "light" ? 1 : 0;
        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел персонализации Windows.");

        key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
        key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);

        NativeThemeMethods.BroadcastImmersiveColorSet();
    }

    public void Toggle() => SetMode(GetMode() == "dark" ? "light" : "dark");
}

internal static class NativeThemeMethods
{
    private const int WmSettingChange = 0x001A;
    private const int SmtoAbortIfHung = 0x0002;
    private static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public static void BroadcastImmersiveColorSet()
    {
        _ = SendMessageTimeout(
            HwndBroadcast,
            WmSettingChange,
            IntPtr.Zero,
            "ImmersiveColorSet",
            SmtoAbortIfHung,
            1000,
            out _);
    }

    [System.Runtime.InteropServices.DllImport(
        "user32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        int msg,
        IntPtr wParam,
        string lParam,
        int flags,
        int timeout,
        out IntPtr result);
}
