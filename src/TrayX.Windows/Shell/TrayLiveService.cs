using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TrayX.Windows.Display;

namespace TrayX.Windows.Shell;

public sealed record TrayLiveIcon(
    string Id,
    string Name,
    string ClassName,
    int MonitorIndex,
    bool OnBar,
    int Left,
    int Top,
    int Width,
    int Height);

/// <summary>
/// Читает настоящий трей Windows 11 через UI Automation: значки на панели и в открытой области скрытых значков.
/// Классический ToolbarWindow32 в новых сборках пуст, поэтому UI Automation — единственный поддерживаемый путь.
/// </summary>
public sealed class TrayLiveService : IDisposable
{
    private const string PrimaryTrayClass = "Shell_TrayWnd";
    private const string SecondaryTrayClass = "Shell_SecondaryTrayWnd";
    private const string OverflowWindowClass = "TopLevelWindowForOverflowXamlIsland";
    private const string BarIconId = "SystemTrayIcon";
    private const string OverflowIconId = "NotifyItemIcon";
    private const string TrayIconClassPrefix = "SystemTray.";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly object _lifecycleSync = new();
    private readonly Dictionary<string, AutomationElement> _elements = new(StringComparer.Ordinal);

    private Thread? _worker;
    private bool _started;
    private bool _disposed;

    public IReadOnlyList<TrayLiveIcon> Scan() => Call(ScanCore);

    public bool Activate(string id) => Call(() => PerformAction(id, false));

    public bool ShowContextMenu(string id) => Call(() => PerformAction(id, true));

    public bool ToggleOverflow() => Call(ToggleOverflowCore);

    public void Start()
    {
        lock (_lifecycleSync)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _worker = new Thread(WorkLoop)
            {
                IsBackground = true,
                Name = "TrayX.TrayLive"
            };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Не удалось запустить поток наблюдения за треем.");
        }
    }

    private void WorkLoop()
    {
        _ready.Set();
        foreach (var job in _queue.GetConsumingEnumerable())
        {
            job();
        }
    }

    private T Call<T>(Func<T> function)
    {
        Start();

        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _queue.Add(() =>
            {
                try
                {
                    source.TrySetResult(function());
                }
                catch (Exception exception)
                {
                    source.TrySetException(exception);
                }
            });
        }

        if (!source.Task.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("Проводник не ответил на запрос трея.");
        }

        return source.Task.GetAwaiter().GetResult();
    }

    private IReadOnlyList<TrayLiveIcon> ScanCore()
    {
        _elements.Clear();
        var icons = new List<TrayLiveIcon>();
        var monitors = ReadMonitors();

        AppendWindow(TrayNative.FindWindow(PrimaryTrayClass), true, monitors, icons);

        var secondary = IntPtr.Zero;
        while (true)
        {
            secondary = TrayNative.FindWindowEx(IntPtr.Zero, secondary, SecondaryTrayClass, null);
            if (secondary == IntPtr.Zero)
            {
                break;
            }

            AppendWindow(secondary, true, monitors, icons);
        }

        var overflow = TrayNative.FindWindow(OverflowWindowClass);
        if (overflow != IntPtr.Zero)
        {
            AppendWindow(overflow, false, monitors, icons);

            // Значки переполнения живут во вложенных XAML-островках: у корневого окна их поддерева нет.
            foreach (var child in TrayNative.ChildWindows(overflow))
            {
                AppendWindow(child, false, monitors, icons);
            }
        }

        return icons;
    }

    private void AppendWindow(
        IntPtr window,
        bool onBar,
        IReadOnlyList<NativeDisplayMethods.Rect> monitors,
        List<TrayLiveIcon> icons)
    {
        var root = FromHandle(window);
        if (root is null)
        {
            return;
        }

        var found = FindByIdentifiers(root);
        if (found.Count == 0)
        {
            // Идентификаторы элементов меняются от сборки к сборке, классы значков — стабильнее.
            found = FindByClassName(root);
        }

        foreach (var (element, name, className, rectangle) in found)
        {
            var centerX = (int)rectangle.X + (int)rectangle.Width / 2;
            var centerY = (int)rectangle.Y + (int)rectangle.Height / 2;
            var monitor = MonitorIndex(monitors, centerX, centerY);
            var id = BuildId(monitor, className, centerX, centerY);

            // Одно и то же поддерево приходит и от корневого острова, и от вложенных окон.
            if (!_elements.TryAdd(id, element))
            {
                continue;
            }

            icons.Add(new TrayLiveIcon(
                id,
                name,
                className,
                monitor,
                onBar,
                (int)rectangle.X,
                (int)rectangle.Y,
                (int)rectangle.Width,
                (int)rectangle.Height));
        }
    }

    private static List<TrayIconCandidate> FindByIdentifiers(AutomationElement root)
    {
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, BarIconId),
            new PropertyCondition(AutomationElement.AutomationIdProperty, OverflowIconId));

        return Collect(root.FindAll(TreeScope.Descendants, condition));
    }

    private static List<TrayIconCandidate> FindByClassName(AutomationElement root)
    {
        var candidates = new List<TrayIconCandidate>();
        foreach (AutomationElement item in root.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try
            {
                if (!item.Current.ClassName.StartsWith(TrayIconClassPrefix, StringComparison.Ordinal) ||
                    item.Current.ControlType != ControlType.Button)
                {
                    continue;
                }

                Add(candidates, item);
            }
            catch (Exception exception) when (IsStale(exception))
            {
            }
        }

        return candidates;
    }

    private static List<TrayIconCandidate> Collect(AutomationElementCollection items)
    {
        var candidates = new List<TrayIconCandidate>();
        foreach (AutomationElement item in items)
        {
            Add(candidates, item);
        }

        return candidates;
    }

    private static void Add(List<TrayIconCandidate> candidates, AutomationElement element)
    {
        try
        {
            var rectangle = element.Current.BoundingRectangle;
            if (rectangle.IsEmpty || rectangle.Width < 4 || rectangle.Height < 4)
            {
                return;
            }

            candidates.Add(new TrayIconCandidate(
                element,
                element.Current.Name,
                element.Current.ClassName,
                rectangle));
        }
        catch (Exception exception) when (IsStale(exception))
        {
        }
    }

    private bool PerformAction(string id, bool rightButton)
    {
        if (!_elements.TryGetValue(id, out var element))
        {
            ScanCore();
            _elements.TryGetValue(id, out element);
        }

        if (element is null)
        {
            return false;
        }

        if (!rightButton)
        {
            return InvokeElement(element);
        }

        var rectangle = ReadRectangle(element);
        if (rectangle.IsEmpty)
        {
            return false;
        }

        return RightClickAt(
            (int)rectangle.X + (int)rectangle.Width / 2,
            (int)rectangle.Y + (int)rectangle.Height / 2);
    }

    private static bool InvokeElement(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var candidate))
        {
            return false;
        }

        ((InvokePattern)candidate).Invoke();
        return true;
    }

    private bool ToggleOverflowCore()
    {
        var chevron = FindChevron();
        return chevron is not null && InvokeElement(chevron.Value.Element);
    }

    private static (AutomationElement Element, bool OnBar)? FindChevron()
    {
        var root = FromHandle(TrayNative.FindWindow(PrimaryTrayClass));
        if (root is null)
        {
            return null;
        }

        AutomationElement? fallback = null;
        var fallbackX = double.MaxValue;

        foreach (var (element, name, className, rectangle) in FindByClassName(root))
        {
            if (!className.EndsWith(".NormalButton", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.Contains("скрыт", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("hidden", StringComparison.OrdinalIgnoreCase))
            {
                return (element, true);
            }

            if (rectangle.X < fallbackX)
            {
                fallbackX = rectangle.X;
                fallback = element;
            }
        }

        return fallback is null ? null : (fallback, true);
    }

    private static System.Windows.Rect ReadRectangle(AutomationElement element)
    {
        try
        {
            return element.Current.BoundingRectangle;
        }
        catch (Exception exception) when (IsStale(exception))
        {
            return System.Windows.Rect.Empty;
        }
    }

    private static AutomationElement? FromHandle(IntPtr window)
    {
        if (window == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return AutomationElement.FromHandle(window);
        }
        catch (Exception exception) when (IsStale(exception))
        {
            return null;
        }
    }

    private static IReadOnlyList<NativeDisplayMethods.Rect> ReadMonitors()
    {
        var monitors = new List<NativeDisplayMethods.Rect>();
        NativeDisplayMethods.EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (IntPtr handle, IntPtr hdc, ref NativeDisplayMethods.Rect rect, IntPtr data) =>
            {
                monitors.Add(rect);
                return true;
            },
            IntPtr.Zero);

        return monitors
            .OrderBy(rect => rect.Left)
            .ThenBy(rect => rect.Top)
            .ToList();
    }

    private static int MonitorIndex(IReadOnlyList<NativeDisplayMethods.Rect> monitors, int x, int y)
    {
        for (var index = 0; index < monitors.Count; index++)
        {
            var rect = monitors[index];
            if (x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom)
            {
                return index;
            }
        }

        return 0;
    }

    private static string BuildId(int monitor, string className, int x, int y)
    {
        // Подсказки меняются каждую минуту (часы, уведомления), поэтому в идентификатор берём только класс и позицию.
        return $"live:{monitor}|{className}|{x}x{y}";
    }

    private static bool RightClickAt(int x, int y)
    {
        if (!TrayNative.GetCursorPos(out var previous) || !TrayNative.SetCursorPos(x, y))
        {
            return false;
        }

        // Реальный правый клик по значку: приложение само показывает своё меню.
        SendMouseButton(TrayNative.MouseEventRightDown);
        SendMouseButton(TrayNative.MouseEventRightUp);
        _ = TrayNative.SetCursorPos(previous.X, previous.Y);
        return true;
    }

    private static void SendMouseButton(uint flags)
    {
        var input = new TrayNative.Input
        {
            Type = TrayNative.InputMouse,
            Mouse = new TrayNative.MouseInput
            {
                Flags = flags,
                Time = 0,
                ExtraInfo = IntPtr.Zero
            }
        };

        _ = TrayNative.SendInput(1, [input], TrayNative.Input.SizeOf());
    }

    private static bool IsStale(Exception exception) =>
        exception is ElementNotAvailableException or
            ElementNotEnabledException or
            InvalidOperationException or
            COMException;

    public void Dispose()
    {
        Thread? worker;
        lock (_lifecycleSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (!_queue.IsAddingCompleted)
            {
                _queue.CompleteAdding();
            }

            worker = _worker;
        }

        if (worker is not null)
        {
            if (!worker.Join(TimeSpan.FromSeconds(12)))
            {
                throw new TimeoutException("Поток UI Automation не завершился после остановки.");
            }
        }
        _queue.Dispose();
        _ready.Dispose();
    }
}

internal readonly struct TrayIconCandidate(
    AutomationElement element,
    string name,
    string className,
    System.Windows.Rect rectangle)
{
    public AutomationElement Element { get; } = element;

    public string Name { get; } = name;

    public string ClassName { get; } = className;

    public System.Windows.Rect Rectangle { get; } = rectangle;

    public void Deconstruct(out AutomationElement element, out string name, out string className, out System.Windows.Rect rectangle)
    {
        element = Element;
        name = Name;
        className = ClassName;
        rectangle = Rectangle;
    }
}

internal static class TrayNative
{
    public const uint MouseEventRightDown = 0x0008;
    public const uint MouseEventRightUp = 0x0010;
    public const uint InputMouse = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public MouseInput Mouse;

        public static int SizeOf() => Marshal.SizeOf<Input>();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string className, string? windowName = null);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, Input[] inputs, int size);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr data);

    public static List<IntPtr> ChildWindows(IntPtr parent)
    {
        var windows = new List<IntPtr>();
        _ = EnumChildWindows(parent, (window, data) =>
        {
            windows.Add(window);
            return true;
        }, IntPtr.Zero);

        return windows;
    }
}
