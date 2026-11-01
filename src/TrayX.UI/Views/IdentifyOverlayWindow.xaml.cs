using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using TrayX.Contracts;
using TrayX.UI.Localization;

namespace TrayX.UI.Views;

public partial class IdentifyOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int SwpNoActivate = 0x0010;
    private const int SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly DisplayDto _display;

    public IdentifyOverlayWindow(DisplayDto display)
    {
        _display = display;
        InitializeComponent();

        NumberText.Text = (display.Index + 1).ToString();
        CaptionText.Text = $"{Loc.T("Displays_OverlayMonitor")} {display.Index + 1}";
        PrimaryBadge.Visibility = display.IsPrimary ? Visibility.Visible : Visibility.Collapsed;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExToolWindow | WsExNoActivate);
        _ = SetWindowPos(
            handle,
            HwndTopmost,
            _display.X,
            _display.Y,
            _display.Width,
            _display.Height,
            SwpNoActivate | SwpShowWindow);

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
