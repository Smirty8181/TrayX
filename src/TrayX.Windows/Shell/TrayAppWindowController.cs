using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using TrayX.Application.TrayApps;
using TrayX.Contracts;

namespace TrayX.Windows.Shell;

public sealed class TrayAppWindowController
{
    private const int ShowNormal = 1;
    private const int ShowMinimized = 2;
    private const int ShowMaximized = 3;
    private const int ShowMinimizedNoActivate = 7;
    private const int ShowNoActivate = 4;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformation = 20;
    private const uint OwnerRelationship = 4;

    private readonly TrayAppWindowCatalog _catalog;
    private readonly ITrayAppRecoveryJournal _journal;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public TrayAppWindowController(
        TrayAppWindowCatalog catalog,
        ITrayAppRecoveryJournal journal)
    {
        _catalog = catalog;
        _journal = journal;
    }

    public async Task<bool> HideAsync(
        TrayAppWindowDto window,
        bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await HideCoreAsync(window, automatic, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<bool> HideCoreAsync(
        TrayAppWindowDto window,
        bool automatic,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryGetWindowIdentity(window, out var processStartTimeUtcTicks, out var showCommand))
        {
            return false;
        }

        var record = new TrayAppRecoveryRecord(
            window.Handle,
            window.ProcessId,
            processStartTimeUtcTicks,
            window.WindowClass,
            window.Title,
            showCommand,
            DateTimeOffset.UtcNow)
        {
            IsAutomatic = automatic
        };

        await _journal.PrepareAsync(record, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!IsSameWindow(record))
        {
            return false;
        }

        var handle = new IntPtr(record.WindowHandle);
        _ = ShowWindowAsync(handle, ShowHide);
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSameWindow(record))
            {
                return false;
            }

            if (!IsWindowVisible(handle))
            {
                return true;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<int> RestorePendingAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
            return await RestoreRecordsAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<int> RestoreWindowAsync(
        TrayAppRestoreWindowRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
            var record = records.FirstOrDefault(candidate =>
                candidate.WindowHandle == request.WindowHandle &&
                candidate.ProcessId == request.ProcessId &&
                candidate.ProcessStartTimeUtcTicks == request.ProcessStartTimeUtcTicks);
            return record is null
                ? 0
                : await RestoreRecordsAsync([record], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<int> RestoreAutomaticPendingAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
            return await RestoreRecordsAsync(
                records.Where(record => record.IsAutomatic).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<int> RestoreManualPendingAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = await _journal.GetPendingAsync(cancellationToken).ConfigureAwait(false);
            return await RestoreRecordsAsync(
                records.Where(record => !record.IsAutomatic).ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    public async Task<int> RestoreAutomaticRecordAsync(
        TrayAppRecoveryRecord record,
        CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ArgumentNullException.ThrowIfNull(record);
            if (!record.IsAutomatic)
            {
                return 0;
            }

            return await RestoreRecordsAsync([record], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operations.Release();
        }
    }

    private async Task<int> RestoreRecordsAsync(
        IReadOnlyList<TrayAppRecoveryRecord> records,
        CancellationToken cancellationToken)
    {
        var restored = 0;
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRecoveryTargetGone(record))
            {
                await _journal.CompleteAsync(
                    record.WindowHandle,
                    record.ProcessId,
                    record.ProcessStartTimeUtcTicks,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!IsSameWindow(record))
            {
                continue;
            }

            var handle = new IntPtr(record.WindowHandle);
            var command = record.WindowShowCommand switch
            {
                ShowMinimized => ShowMinimizedNoActivate,
                ShowMaximized => ShowMaximized,
                _ => ShowNoActivate
            };
            _ = ShowWindowAsync(handle, command);
            var visible = false;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsSameWindow(record))
                {
                    break;
                }

                if (IsWindowVisible(handle))
                {
                    visible = true;
                    break;
                }

                await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            }

            if (!visible)
            {
                continue;
            }

            await _journal.CompleteAsync(
                record.WindowHandle,
                record.ProcessId,
                record.ProcessStartTimeUtcTicks,
                cancellationToken).ConfigureAwait(false);
            restored++;
        }

        return restored;
    }

    private bool TryGetWindowIdentity(
        TrayAppWindowDto window,
        out long processStartTimeUtcTicks,
        out int showCommand)
    {
        processStartTimeUtcTicks = 0;
        showCommand = 0;

        var current = _catalog.GetWindow(window.Handle);
        if (current is null ||
            current.ProcessId != window.ProcessId ||
            !string.Equals(current.WindowClass, window.WindowClass, StringComparison.Ordinal) ||
            !string.Equals(current.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase) ||
            GetWindow(new IntPtr(window.Handle), OwnerRelationship) != IntPtr.Zero ||
            IsElevatedProcess(window.ProcessId) && !IsCurrentProcessElevated() ||
            !IsWindowVisible(new IntPtr(window.Handle)))
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(window.ProcessId);
            processStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }

        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(new IntPtr(window.Handle), ref placement))
        {
            return false;
        }

        showCommand = placement.ShowCommand switch
        {
            ShowMinimized or ShowMinimizedNoActivate => ShowMinimized,
            ShowMaximized => ShowMaximized,
            _ => ShowNormal
        };
        return true;
    }

    private bool IsSameWindow(TrayAppRecoveryRecord record)
    {
        var handle = new IntPtr(record.WindowHandle);
        if (!IsWindow(handle))
        {
            return false;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId != record.ProcessId)
        {
            return false;
        }

        var className = new StringBuilder(256);
        if (GetClassName(handle, className, className.Capacity) <= 0 ||
            !string.Equals(className.ToString(), record.WindowClass, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(record.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == record.ProcessStartTimeUtcTicks;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsRecoveryTargetGone(TrayAppRecoveryRecord record)
    {
        var handle = new IntPtr(record.WindowHandle);
        if (!IsWindow(handle))
        {
            return true;
        }

        _ = GetWindowThreadProcessId(handle, out var processId);
        if (processId != record.ProcessId)
        {
            return true;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(record.ProcessId);
            return process.StartTime.ToUniversalTime().Ticks != record.ProcessStartTimeUtcTicks;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsCurrentProcessElevated() => IsElevatedProcess(Environment.ProcessId);

    private static bool IsElevatedProcess(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return true;
            }

            try
            {
                var elevation = new TokenElevation();
                return GetTokenInformation(
                    token,
                    TokenElevationInformation,
                    ref elevation,
                    Marshal.SizeOf<TokenElevation>(),
                    out _)
                    ? elevation.IsElevated != 0
                    : true;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private const int ShowHide = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinimumPosition;
        public NativePoint MaximumPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int IsElevated;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr window, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token,
        int informationClass,
        ref TokenElevation information,
        int informationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);
}
