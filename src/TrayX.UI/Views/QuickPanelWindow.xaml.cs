using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TrayX.UI.Views;

public partial class QuickPanelWindow : Window
{
    private const int SwpNoSize = 0x0001;
    private const int SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    private bool _forceClose;

    public QuickPanelWindow()
    {
        InitializeComponent();
    }

    public void ShowPanel()
    {
        if (!IsVisible)
        {
            Show();
        }

        UpdateLayout();
        PlaceNearCursorMonitor();
        Activate();
    }

    public void ForceClose()
    {
        _forceClose = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_forceClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_forceClose)
        {
            Hide();
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void PlaceNearCursorMonitor()
    {
        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = MonitorFromPoint(cursor, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var window))
        {
            return;
        }

        const int margin = 14;
        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var work = info.Work;

        var x = Math.Max(work.Left, work.Right - width - margin);
        var y = Math.Max(work.Top, work.Bottom - height - margin);

        _ = SetWindowPos(handle, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public int Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);
}
