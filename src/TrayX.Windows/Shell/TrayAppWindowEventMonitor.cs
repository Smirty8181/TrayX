using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TrayX.Windows.Shell;

public sealed class TrayAppWindowEventMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint ObjIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint WmQuit = 0x0012;

    private readonly Action _changed;
    private readonly Action<Exception> _onError;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Timer _debounce;
    private readonly WinEventProc _callback;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _objectHook;
    private IntPtr _foregroundHook;
    private Exception? _startupFailure;
    private bool _disposed;

    public TrayAppWindowEventMonitor(Action changed, Action<Exception> onError)
    {
        _changed = changed ?? throw new ArgumentNullException(nameof(changed));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _callback = OnWinEvent;
        _debounce = new Timer(OnDebounceElapsed);
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "TrayX.TrayApps.WinEvents"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Tray app window event monitoring did not start.");
        }

        if (_startupFailure is not null)
        {
            throw new InvalidOperationException("Tray app window event monitoring failed to start.", _startupFailure);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _debounce.Dispose();
        if (_thread is not null && _thread.IsAlive)
        {
            if (!PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                if (_thread.IsAlive)
                {
                    throw new Win32Exception(error, "Could not stop the tray app window event thread.");
                }
            }

            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Tray app window event thread did not stop.");
            }
        }

        _ready.Dispose();
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            _objectHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectNameChange,
                IntPtr.Zero,
                _callback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
            if (_objectHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not subscribe to window events.");
            }

            _foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                _callback,
                0,
                0,
                WineventOutOfContext | WineventSkipOwnProcess);
            if (_foregroundHook == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not subscribe to foreground window events.");
            }

            _ = PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            _ready.Set();
            while (true)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Tray app window event message loop failed.");
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            if (!_ready.IsSet)
            {
                _startupFailure = exception;
            }
            else
            {
                _onError(exception);
            }
        }
        finally
        {
            _ready.Set();
            if (_foregroundHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_foregroundHook);
                _foregroundHook = IntPtr.Zero;
            }

            if (_objectHook != IntPtr.Zero)
            {
                _ = UnhookWinEvent(_objectHook);
                _objectHook = IntPtr.Zero;
            }
        }
    }

    private void OnWinEvent(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (window == IntPtr.Zero)
        {
            return;
        }

        if (eventType != EventSystemForeground &&
            (objectId != ObjIdWindow || childId != ChildIdSelf))
        {
            return;
        }

        try
        {
            _debounce.Change(200, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void OnDebounceElapsed(object? state)
    {
        try
        {
            _changed();
        }
        catch (Exception exception)
        {
            _onError(exception);
        }
    }

    private delegate void WinEventProc(
        IntPtr hook,
        uint eventType,
        IntPtr window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public NativePoint Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr module,
        WinEventProc callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        IntPtr window,
        uint min,
        uint max,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);
}
