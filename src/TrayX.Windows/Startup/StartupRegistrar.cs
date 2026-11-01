using Microsoft.Win32;

namespace TrayX.Windows.Startup;

public sealed class StartupRegistrar
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupRegistrar(string valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valueName);
        ValueName = valueName;
    }

    public string ValueName { get; }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    public void SetEnabled(bool enabled, string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
            ?? throw new InvalidOperationException("Не удалось открыть раздел автозапуска Windows.");

        if (enabled)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
            key.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
