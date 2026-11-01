using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TrayX.Contracts;

namespace TrayX.Windows.Display;

public sealed class DisplayService
{
    private readonly object _brightnessSync = new();
    private readonly Dictionary<string, (int? Value, DateTimeOffset Timestamp)> _brightnessCache =
        new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DisplayDto> GetDisplays()
    {
        var monitors = new List<NativeDisplayMethods.MonitorEntry>();
        NativeDisplayMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeDisplayMethods.Rect rect, IntPtr data) =>
            {
                monitors.Add(NativeDisplayMethods.Describe(monitor));
                return true;
            },
            IntPtr.Zero);

        var result = new List<DisplayDto>(monitors.Count);
        var ordered = monitors
            .OrderBy(item => item.Bounds.Left)
            .ThenBy(item => item.Bounds.Top)
            .ToList();
        var stableIds = GetStableIds(ordered);

        for (var index = 0; index < ordered.Count; index++)
        {
            var monitor = ordered[index];
            var device = NativeDisplayMethods.GetDisplayDevice(monitor.DeviceName);
            var mode = NativeDisplayMethods.GetCurrentMode(monitor.DeviceName);
            var (dpiX, dpiY) = NativeDisplayMethods.GetDpi(monitor.Handle);

            result.Add(new DisplayDto(
                index,
                monitor.DeviceName,
                ResolveFriendlyName(device, monitor.DeviceName),
                monitor.Bounds.Left,
                monitor.Bounds.Top,
                monitor.Bounds.Right - monitor.Bounds.Left,
                monitor.Bounds.Bottom - monitor.Bounds.Top,
                monitor.WorkArea.Left,
                monitor.WorkArea.Top,
                monitor.WorkArea.Right - monitor.WorkArea.Left,
                monitor.WorkArea.Bottom - monitor.WorkArea.Top,
                monitor.IsPrimary,
                dpiX,
                dpiY,
                mode.Orientation,
                mode.RefreshRate,
                mode.BitsPerPixel,
                GetBrightnessCached(monitor.Handle, monitor.DeviceName),
                stableIds[index]));
        }

        return result;
    }

    public int SetBrightness(int displayIndex, int percent, string? stableId = null)
    {
        var monitors = new List<NativeDisplayMethods.MonitorEntry>();
        NativeDisplayMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr monitor, IntPtr hdc, ref NativeDisplayMethods.Rect rect, IntPtr data) =>
            {
                monitors.Add(NativeDisplayMethods.Describe(monitor));
                return true;
            },
            IntPtr.Zero);

        var ordered = monitors
            .OrderBy(item => item.Bounds.Left)
            .ThenBy(item => item.Bounds.Top)
            .ToList();

        if (!string.IsNullOrWhiteSpace(stableId))
        {
            displayIndex = Array.FindIndex(
                GetStableIds(ordered),
                candidate => string.Equals(candidate, stableId, StringComparison.OrdinalIgnoreCase));
            if (displayIndex < 0)
            {
                throw new InvalidOperationException("Монитор отключён или изменился. Обновите список дисплеев.");
            }
        }

        if (displayIndex < 0 || displayIndex >= ordered.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(displayIndex), "Монитор не найден.");
        }

        var target = ordered[displayIndex];
        var clamped = Math.Clamp(percent, 0, 100);

        if (!NativeDisplayMethods.TrySetBrightness(target.Handle, (uint)clamped, out var error))
        {
            throw new InvalidOperationException(
                $"Монитор {ResolveFriendlyName(NativeDisplayMethods.GetDisplayDevice(target.DeviceName), target.DeviceName)} " +
                $"не поддерживает управление яркостью через DDC/CI. {error}".Trim());
        }

        lock (_brightnessSync)
        {
            _brightnessCache[target.DeviceName] = (clamped, DateTimeOffset.UtcNow);
        }

        return clamped;
    }

    private static string[] GetStableIds(IReadOnlyList<NativeDisplayMethods.MonitorEntry> monitors)
    {
        var ids = monitors.Select(monitor =>
        {
            var deviceId = NativeDisplayMethods.GetDisplayDevice(monitor.DeviceName).DeviceID?.Trim();
            return string.IsNullOrWhiteSpace(deviceId) ? monitor.DeviceName : deviceId;
        }).ToArray();

        var duplicateIds = ids
            .Select((id, index) => (Id: id, DeviceName: monitors[index].DeviceName))
            .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => item.DeviceName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ids.Length; index++)
        {
            if (duplicateIds.Contains(monitors[index].DeviceName))
            {
                ids[index] = $"{ids[index]}|{monitors[index].DeviceName}";
            }
        }

        return ids;
    }

    public static void OpenWindowsDisplaySettings()
    {
        using var process = Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true });
    }

    private int? GetBrightnessCached(IntPtr monitor, string deviceName)
    {
        lock (_brightnessSync)
        {
            if (_brightnessCache.TryGetValue(deviceName, out var cached) &&
                DateTimeOffset.UtcNow - cached.Timestamp < TimeSpan.FromSeconds(15))
            {
                return cached.Value;
            }
        }

        var value = NativeDisplayMethods.TryGetBrightness(monitor, out var brightness, out _)
            ? brightness
            : null;

        lock (_brightnessSync)
        {
            _brightnessCache[deviceName] = (value, DateTimeOffset.UtcNow);
        }

        return value;
    }

    private static string ResolveFriendlyName(NativeDisplayMethods.DisplayDevice device, string fallback)
    {
        var name = device.DeviceString?.Trim();
        if (!string.IsNullOrWhiteSpace(name) &&
            !name.Equals("Generic PnP Monitor", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Универсальный монитор PnP", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        return string.IsNullOrWhiteSpace(name) ? fallback : $"{name} · {fallback}";
    }
}

internal static class NativeDisplayMethods
{
    public const int EnumCurrentSettings = -1;
    private const int MonitorInfoPrimary = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public sealed record MonitorEntry(
        IntPtr Handle,
        string DeviceName,
        Rect Bounds,
        Rect WorkArea,
        bool IsPrimary);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDevice
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;

        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;

        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public readonly record struct DisplayMode(string Orientation, int RefreshRate, int BitsPerPixel);

    internal delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(
        string? device,
        uint deviceNumber,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode devMode);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static MonitorEntry Describe(IntPtr monitor)
    {
        var info = new MonitorInfoEx
        {
            cbSize = Marshal.SizeOf<MonitorInfoEx>(),
            szDevice = string.Empty
        };

        if (!GetMonitorInfo(monitor, ref info))
        {
            info.szDevice = string.Empty;
        }

        return new MonitorEntry(
            monitor,
            info.szDevice,
            info.rcMonitor,
            info.rcWork,
            (info.dwFlags & MonitorInfoPrimary) != 0);
    }

    public static DisplayDevice GetDisplayDevice(string deviceName)
    {
        var device = new DisplayDevice
        {
            cb = Marshal.SizeOf<DisplayDevice>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty
        };

        _ = EnumDisplayDevices(deviceName, 0, ref device, 0);
        return device;
    }

    public static DisplayMode GetCurrentMode(string deviceName)
    {
        var mode = new DevMode
        {
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
            dmSize = (ushort)Marshal.SizeOf<DevMode>()
        };

        if (!EnumDisplaySettings(deviceName, EnumCurrentSettings, ref mode))
        {
            return new DisplayMode("landscape", 0, 0);
        }

        var orientation = mode.dmDisplayOrientation switch
        {
            1 => "portrait",
            2 => "landscape-flipped",
            3 => "portrait-flipped",
            _ => "landscape"
        };

        return new DisplayMode(orientation, (int)mode.dmDisplayFrequency, (int)mode.dmBitsPerPel);
    }

    public static (uint X, uint Y) GetDpi(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, 0, out var x, out var y) == 0 ? (x, y) : (96u, 96u);
        }
        catch (DllNotFoundException)
        {
            return (96, 96);
        }
        catch (EntryPointNotFoundException)
        {
            return (96, 96);
        }
    }

    public static bool TryGetBrightness(IntPtr monitor, out int? percent, out string? error)
    {
        percent = null;
        error = null;

        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
        {
            error = "Монитор не сообщает физические каналы.";
            return false;
        }

        var physical = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            if (physical.Length == 0)
            {
                return false;
            }

            if (!GetMonitorBrightness(physical[0].Handle, out _, out var current, out _))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            percent = (int)current;
            return true;
        }
        finally
        {
            _ = DestroyPhysicalMonitors(count, physical);
        }
    }

    public static bool TrySetBrightness(IntPtr monitor, uint percent, out string? error)
    {
        error = null;
        if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
        {
            error = "DDC/CI недоступен.";
            return false;
        }

        var physical = new PhysicalMonitor[count];
        if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical) || physical.Length == 0)
        {
            error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return false;
        }

        try
        {
            if (!GetMonitorBrightness(physical[0].Handle, out _, out var current, out var maximum) ||
                maximum == 0)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            var target = (uint)Math.Round(percent / 100.0 * maximum);
            if (!SetMonitorBrightness(physical[0].Handle, target))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (!GetMonitorBrightness(physical[0].Handle, out _, out var applied, out _) ||
                Math.Abs((long)applied - target) > 2)
            {
                error = "Монитор не подтвердил изменение яркости.";
                return false;
            }

            return true;
        }
        finally
        {
            _ = DestroyPhysicalMonitors(count, physical);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(
        IntPtr monitor,
        uint count,
        [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(
        IntPtr monitor,
        out uint minimum,
        out uint current,
        out uint maximum);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint brightness);

    [DllImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(
        uint count,
        [In] PhysicalMonitor[] monitors);
}
