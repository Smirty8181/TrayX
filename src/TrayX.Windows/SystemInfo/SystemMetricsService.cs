using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using TrayX.Contracts;

namespace TrayX.Windows.SystemInfo;

public sealed class SystemMetricsService
{
    private readonly object _sync = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private ulong _previousIdle;
    private ulong _previousKernel;
    private ulong _previousUser;
    private long _previousRx;
    private long _previousTx;
    private TimeSpan _previousNetworkTime;
    private bool _primed;
    private SystemStatsDto? _cached;
    private long _cachedTicks;

    public SystemStatsDto GetStats()
    {
        lock (_sync)
        {
            var now = Stopwatch.GetTimestamp();
            if (_cached is not null &&
                Stopwatch.GetElapsedTime(_cachedTicks, now) < TimeSpan.FromMilliseconds(500))
            {
                return _cached;
            }

            if (!TryReadCpu(out var idle, out var kernel, out var user))
            {
                idle = _previousIdle;
                kernel = _previousKernel;
                user = _previousUser;
            }

            double cpu = 0;
            if (_primed)
            {
                var idleDelta = idle - _previousIdle;
                var totalDelta = (kernel + user) - (_previousKernel + _previousUser);
                if (totalDelta > 0)
                {
                    cpu = Math.Clamp(100.0 * (1.0 - (double)idleDelta / totalDelta), 0, 100);
                }
            }

            _previousIdle = idle;
            _previousKernel = kernel;
            _previousUser = user;

            var (rx, tx) = ReadNetworkTotals();
            var elapsed = _clock.Elapsed - _previousNetworkTime;
            var inRate = 0.0;
            var outRate = 0.0;
            if (_primed && elapsed.TotalSeconds >= 0.2)
            {
                inRate = Math.Max(0, (rx - _previousRx) / elapsed.TotalSeconds);
                outRate = Math.Max(0, (tx - _previousTx) / elapsed.TotalSeconds);
            }

            _previousRx = rx;
            _previousTx = tx;
            _previousNetworkTime = _clock.Elapsed;

            var memory = ReadMemory();
            var battery = ReadBattery();
            var (freeSpace, totalSpace) = ReadSystemDrive();
            var (processes, processCount) = ReadProcesses();

            var stats = new SystemStatsDto(
                DateTimeOffset.Now,
                _primed ? Math.Round(cpu, 1) : 0,
                memory.Total,
                memory.Used,
                memory.Total == 0 ? 0 : Math.Round(100.0 * memory.Used / memory.Total, 1),
                Math.Round(inRate),
                Math.Round(outRate),
                battery.Percent,
                battery.Charging,
                (long)TimeSpan.FromMilliseconds(Environment.TickCount64).TotalSeconds,
                processCount,
                freeSpace,
                totalSpace,
                processes);

            _primed = true;
            _cached = stats;
            _cachedTicks = now;
            return stats;
        }
    }

    private static bool TryReadCpu(out ulong idle, out ulong kernel, out ulong user)
    {
        idle = 0;
        kernel = 0;
        user = 0;
        if (!NativeSystemMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return false;
        }

        idle = idleTime.ToUInt64();
        kernel = kernelTime.ToUInt64();
        user = userTime.ToUInt64();
        return true;
    }

    private static (long Total, long Used) ReadMemory()
    {
        var status = new MemoryStatusEx();
        status.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (!NativeSystemMethods.GlobalMemoryStatusEx(ref status))
        {
            return (0, 0);
        }

        var total = (long)Math.Min(status.ullTotalPhys, (ulong)long.MaxValue);
        var available = (long)Math.Min(status.ullAvailPhys, (ulong)long.MaxValue);
        return (total, Math.Max(0, total - available));
    }

    private static (long Rx, long Tx) ReadNetworkTotals()
    {
        long rx = 0;
        long tx = 0;
        try
        {
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (network.OperationalStatus != OperationalStatus.Up ||
                    network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var statistics = network.GetIPStatistics();
                rx += statistics.BytesReceived;
                tx += statistics.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
        }

        return (rx, tx);
    }

    private static (int? Percent, bool? Charging) ReadBattery()
    {
        if (!NativeSystemMethods.GetSystemPowerStatus(out var status))
        {
            return (null, null);
        }

        var charging = status.ACLineStatus == 1;
        if (status.BatteryLifePercent == 255 || (status.BatteryFlag & 128) != 0)
        {
            return (null, charging);
        }

        return (status.BatteryLifePercent, charging);
    }

    private static (long Free, long Total) ReadSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(root);
            return (drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (0, 0);
        }
    }

    private static (IReadOnlyList<ProcessUsageDto> Top, int Count) ReadProcesses()
    {
        var groups = new List<ProcessUsageDto>();
        var totals = new Dictionary<string, (long Memory, int Pid)>(StringComparer.OrdinalIgnoreCase);

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (InvalidOperationException)
        {
            return (groups, 0);
        }

        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var workingSet = process.WorkingSet64;
                    if (totals.TryGetValue(name, out var existing))
                    {
                        totals[name] = (existing.Memory + workingSet, existing.Pid);
                    }
                    else
                    {
                        totals[name] = (workingSet, process.Id);
                    }
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or
                    System.ComponentModel.Win32Exception or
                    NotSupportedException)
                {
                }
            }
        }

        var count = totals.Count;
        foreach (var pair in totals
                     .OrderByDescending(item => item.Value.Memory)
                     .Take(5))
        {
            groups.Add(new ProcessUsageDto(pair.Key, pair.Value.Memory, pair.Value.Pid));
        }

        return (groups, count);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    private static class NativeSystemMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    }
}
