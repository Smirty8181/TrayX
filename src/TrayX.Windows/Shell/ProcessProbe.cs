using System.Diagnostics;

namespace TrayX.Windows.Shell;

/// <summary>
/// Имена и PID запущенных процессов. MainModule не трогаем: он стоит дорого и падает на чужих процессах.
/// </summary>
internal static class ProcessProbe
{
    public static Dictionary<string, int> ReadNames()
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                result.TryAdd(process.ProcessName, process.Id);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }
}
