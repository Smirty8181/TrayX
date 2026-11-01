using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using TrayX.Contracts;

namespace TrayX.Windows.Shell;

public sealed class TrayAppWindowCatalog
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ExtendedWindowStyleIndex = -20;
    private const uint OwnerRelationship = 4;
    private const long ToolWindowExtendedStyle = 0x00000080;
    private readonly int? _excludedProcessId;

    private static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "taskmgr.exe",
        "consent.exe",
        "logonui.exe",
        "trayx.core.exe",
        "trayx.ui.exe"
    };

    public TrayAppWindowCatalog()
        : this(includeCurrentProcess: false)
    {
    }

    public TrayAppWindowCatalog(bool includeCurrentProcess)
    {
        _excludedProcessId = includeCurrentProcess ? null : Environment.ProcessId;
    }

    public IReadOnlyList<TrayAppWindowDto> GetVisibleWindows()
    {
        var windows = new List<TrayAppWindowDto>();
        Exception? callbackFailure = null;
        EnumWindowsProc callback = (window, _) =>
        {
            try
            {
                AddWindow(window, windows);
                return true;
            }
            catch (Exception exception)
            {
                // Managed exceptions must not escape a native EnumWindows callback.
                callbackFailure ??= exception;
                return true;
            }
        };

        if (!EnumWindows(callback, IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось перечислить окна Windows.");
        }

        if (callbackFailure is not null)
        {
            throw new InvalidOperationException(
                "Не удалось прочитать информацию об одном или нескольких окнах.",
                callbackFailure);
        }

        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public TrayAppWindowDto? GetWindow(long handle)
    {
        if (handle == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(handle));
        }

        var windows = new List<TrayAppWindowDto>(1);
        AddWindow(new IntPtr(handle), windows);
        return windows.FirstOrDefault();
    }

    private void AddWindow(IntPtr window, ICollection<TrayAppWindowDto> windows)
    {
        var extendedStyle = GetWindowLongPointer(window, ExtendedWindowStyleIndex).ToInt64();
        if (!IsManageableWindow(
                IsWindowVisible(window),
                GetWindow(window, OwnerRelationship) != IntPtr.Zero,
                extendedStyle))
        {
            return;
        }

        var titleLength = GetWindowTextLength(window);
        if (titleLength <= 0)
        {
            return;
        }

        var title = new StringBuilder(Math.Min(titleLength, 2047) + 1);
        _ = GetWindowText(window, title, title.Capacity);
        var caption = title.ToString().Trim();
        if (caption.Length == 0)
        {
            return;
        }

        var className = new StringBuilder(256);
        if (GetClassName(window, className, className.Capacity) <= 0)
        {
            return;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == _excludedProcessId)
        {
            return;
        }

        var processName = GetProcessName(processId);
        if (processName is null || ExcludedProcesses.Contains(processName))
        {
            return;
        }

        windows.Add(new TrayAppWindowDto(
            window.ToInt64(),
            checked((int)processId),
            processName,
            caption,
            className.ToString()));
    }

    internal static bool IsManageableWindow(bool isVisible, bool hasOwner, long extendedStyle) =>
        isVisible && !hasOwner && (extendedStyle & ToolWindowExtendedStyle) == 0;

    private static IntPtr GetWindowLongPointer(IntPtr window, int index) =>
        IntPtr.Size == 8
            ? GetWindowLongPointer64(window, index)
            : new IntPtr(GetWindowLong32(window, index));

    private static string? GetProcessName(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(2048);
            var length = path.Capacity;
            return QueryFullProcessImageName(process, 0, path, ref length)
                ? Path.GetFileName(path.ToString())
                : null;
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPointer64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr window, int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextLength(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
