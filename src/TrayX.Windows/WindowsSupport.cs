namespace TrayX.Windows;

public static class WindowsSupport
{
    public const int MinimumBuild = 22621;

    public static bool IsSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumBuild);
}
