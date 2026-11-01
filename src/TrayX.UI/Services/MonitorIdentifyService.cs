using System.Windows.Threading;
using TrayX.Contracts;
using TrayX.UI.Views;

namespace TrayX.UI.Services;

public static class MonitorIdentifyService
{
    private static readonly List<IdentifyOverlayWindow> Overlays = new();
    private static DispatcherTimer? _timer;

    public static void Show(IEnumerable<DisplayDto> displays)
    {
        CloseAll();

        foreach (var display in displays)
        {
            var window = new IdentifyOverlayWindow(display);
            window.Show();
            Overlays.Add(window);
        }

        if (Overlays.Count == 0)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) => CloseAll();
        _timer.Start();
    }

    public static void CloseAll()
    {
        _timer?.Stop();
        _timer = null;

        foreach (var overlay in Overlays)
        {
            overlay.Close();
        }

        Overlays.Clear();
    }
}
