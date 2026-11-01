using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using TrayX.Contracts;

namespace TrayX.Windows.Shell;

public enum TrayMenuCommand
{
    Open,
    QuickPanel,
    Audio,
    ToggleMute,
    ToggleTheme,
    ToggleOverflow,
    TrayApps,
    RestoreTrayApps,
    Settings,
    Exit
}

public sealed record TrayMenuState(
    bool Muted,
    bool DarkTheme,
    string Language,
    bool UiConnected,
    bool OverflowOpen,
    IReadOnlyList<TrayAppRecoveryRecord> PendingTrayApps);

public sealed class TrayIconHost : IDisposable
{
    private const int WmApp = 0x8000;
    private const int WmAction = WmApp + 10;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const int WmNull = 0x0000;
    private const int WmContextMenu = 0x007B;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int WmRButtonDown = 0x0204;
    private const int WmTimer = 0x0113;
    private const uint RetryIconTimerId = 1;
    private const uint RetryIconIntervalMs = 5000;

    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint NifShowTip = 0x00000080;
    private const uint NiifInfo = 0x00000001;
    private const uint NotifyIconVersion4 = 4;

    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint MfGrayed = 0x00000001;
    private const uint MfPopup = 0x00000010;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNoNotify = 0x0080;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x00000010;
    private const uint LrDefaultSize = 0x0040;

    private const uint MenuOpen = 1;
    private const uint MenuQuick = 2;
    private const uint MenuAudio = 3;
    private const uint MenuMute = 4;
    private const uint MenuTheme = 5;
    private const uint MenuSettings = 6;
    private const uint MenuExit = 7;
    private const uint MenuOverflow = 8;
    private const uint MenuTrayApps = 9;
    private const uint MenuRestoreTrayApps = 10;
    private const uint MenuRestoreWindowBase = 1000;
    private const int MaxRestoreWindowMenuItems = 20;

    private readonly Action<TrayMenuCommand> _commandHandler;
    private readonly Action<TrayAppRecoveryRecord> _restoreWindowHandler;
    private readonly Func<TrayMenuState> _stateProvider;
    private readonly Action _leftClickHandler;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly string _iconPath;
    private readonly string _tooltip;

    private Thread? _thread;
    private IntPtr _window;
    private IntPtr _icon;
    private bool _ownsIcon;
    private bool _enabled;
    private bool _iconAdded;
    private bool _retryTimerActive;
    private bool _retryFailureLogged;
    private bool _disposed;
    private Exception? _startupFailure;
    private ushort _windowClass;
    private uint _taskbarCreatedMessage;
    private NativeShellMethods.WndProcDelegate? _windowProc;
    private string _windowClassName = string.Empty;

    public TrayIconHost(
        string iconPath,
        string tooltip,
        Action<TrayMenuCommand> commandHandler,
        Action<TrayAppRecoveryRecord> restoreWindowHandler,
        Func<TrayMenuState> stateProvider,
        Action leftClickHandler,
        Action<string>? log = null)
    {
        _iconPath = iconPath;
        _tooltip = tooltip;
        _commandHandler = commandHandler;
        _restoreWindowHandler = restoreWindowHandler;
        _stateProvider = stateProvider;
        _leftClickHandler = leftClickHandler;
        _log = log ?? (_ => { });
    }

    public void Start(bool enabled)
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(() => Run(enabled))
        {
            IsBackground = true,
            Name = "TrayX.TrayIcon"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Не удалось запустить собственный значок трея.");
        }

        if (_startupFailure is { } startupFailure)
        {
            ExceptionDispatchInfo.Capture(startupFailure).Throw();
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed)
        {
            return;
        }

        _actions.Enqueue(() => ApplyEnabled(enabled));
        PostAction();
    }

    public void ShowNotification(string title, string message)
    {
        if (_disposed)
        {
            return;
        }

        _actions.Enqueue(() => ShowBalloon(title, message));
        PostAction();
    }

    public void UpdateTooltip(string tooltip)
    {
        if (_disposed)
        {
            return;
        }

        _actions.Enqueue(() => ModifyTooltip(tooltip));
        PostAction();
    }

    private void PostAction()
    {
        var window = _window;
        if (window != IntPtr.Zero)
        {
            _ = NativeShellMethods.PostMessage(window, WmAction, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Run(bool enabled)
    {
        try
        {
            _enabled = enabled;
            var module = NativeShellMethods.GetModuleHandle(null);
            _windowClassName = "TrayX.TrayIconHost." + Guid.NewGuid().ToString("N");
            _windowProc = WindowProc;
            _windowClass = NativeShellMethods.RegisterClass(module, _windowClassName, _windowProc);
            if (_windowClass == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx завершился ошибкой.");
            }

            _taskbarCreatedMessage = NativeShellMethods.RegisterWindowMessage("TaskbarCreated");
            _window = NativeShellMethods.CreateWindowEx(
                0,
                _windowClassName,
                "TrayX",
                0,
                0,
                0,
                0,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                module,
                IntPtr.Zero);

            if (_window == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx завершился ошибкой.");
            }

            _icon = LoadIcon();
            if (_icon == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось загрузить значок TrayX.");
            }

            if (_enabled)
            {
                _ = AddIcon();
            }

            _ready.Set();

            while (NativeShellMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = NativeShellMethods.TranslateMessage(ref message);
                _ = NativeShellMethods.DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _startupFailure = exception;
            _log($"Tray icon failed: {exception}");
            _ready.Set();
        }
        finally
        {
            Cleanup();
            _ready.Set();
        }
    }

    private IntPtr LoadIcon()
    {
        if (File.Exists(_iconPath))
        {
            var loaded = NativeShellMethods.LoadImage(
                IntPtr.Zero,
                _iconPath,
                ImageIcon,
                0,
                0,
                LrLoadFromFile | LrDefaultSize);

            if (loaded != IntPtr.Zero)
            {
                _ownsIcon = true;
                return loaded;
            }
        }

        return NativeShellMethods.LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private void ApplyEnabled(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (_enabled)
        {
            AddIcon();
        }
        else
        {
            StopIconRetry();
            _retryFailureLogged = false;
            RemoveIcon();
        }
    }

    private bool AddIcon()
    {
        if (_window == IntPtr.Zero || _iconAdded)
        {
            return _iconAdded;
        }

        var data = CreateIconData();
        data.uFlags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.uCallbackMessage = WmApp + 1;
        data.hIcon = _icon;
        data.szTip = _tooltip;

        if (NativeShellMethods.ShellNotifyIcon(NimAdd, ref data))
        {
            _iconAdded = true;
            SetIconVersion();
            _retryFailureLogged = false;
            StopIconRetry();
            return true;
        }

        if (!_retryFailureLogged)
        {
            _log($"Shell_NotifyIcon(NIM_ADD) failed: {Marshal.GetLastWin32Error()}; retrying.");
            _retryFailureLogged = true;
        }

        StartIconRetry();
        return false;
    }

    private void SetIconVersion()
    {
        var data = CreateIconData();
        data.uTimeoutOrVersion = NotifyIconVersion4;
        if (!NativeShellMethods.ShellNotifyIcon(NimSetVersion, ref data))
        {
            _log($"Shell_NotifyIcon(NIM_SETVERSION) failed: {Marshal.GetLastWin32Error()}.");
        }
    }

    private void RemoveIcon()
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        _ = NativeShellMethods.ShellNotifyIcon(NimDelete, ref data);
        _iconAdded = false;
    }

    private void StartIconRetry()
    {
        if (!_enabled || _retryTimerActive || _window == IntPtr.Zero)
        {
            return;
        }

        var timer = NativeShellMethods.SetTimer(
            _window,
            new UIntPtr(RetryIconTimerId),
            RetryIconIntervalMs,
            IntPtr.Zero);
        if (timer == UIntPtr.Zero)
        {
            _log($"Tray icon retry timer could not start: {Marshal.GetLastWin32Error()}");
            return;
        }

        _retryTimerActive = true;
    }

    private void StopIconRetry()
    {
        if (!_retryTimerActive || _window == IntPtr.Zero)
        {
            return;
        }

        _ = NativeShellMethods.KillTimer(_window, new UIntPtr(RetryIconTimerId));
        _retryTimerActive = false;
    }

    private void ModifyTooltip(string tooltip)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        data.uFlags = NifTip;
        data.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _ = NativeShellMethods.ShellNotifyIcon(NimModify, ref data);
    }

    private void ShowBalloon(string title, string message)
    {
        if (!_iconAdded)
        {
            return;
        }

        var data = CreateIconData();
        data.uFlags = NifInfo | NifTip | NifShowTip;
        data.szTip = _tooltip;
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = message.Length > 255 ? message[..255] : message;
        data.dwInfoFlags = NiifInfo;
        _ = NativeShellMethods.ShellNotifyIcon(NimModify, ref data);
    }

    private NotifyIconData CreateIconData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty
    };

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmAction:
                while (_actions.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception exception)
                    {
                        _log($"Tray action failed: {exception}");
                    }
                }

                return IntPtr.Zero;

            case WmApp + 1:
            {
                var notification = (uint)(lParam.ToInt64() & 0xFFFF);
                if (notification == WmLeftButtonUp)
                {
                    InvokeLeftClick();
                }
                else if (notification is WmRightButtonUp or WmContextMenu)
                {
                    ShowMenu();
                }
                else if (notification == WmRButtonDown)
                {
                    _ = NativeShellMethods.SetForegroundWindow(window);
                }

                return IntPtr.Zero;
            }

            case WmClose:
                _ = NativeShellMethods.DestroyWindow(window);
                return IntPtr.Zero;

            case WmDestroy:
                NativeShellMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case WmTimer when unchecked((uint)wParam.ToInt64()) == RetryIconTimerId:
                if (_enabled && !_iconAdded)
                {
                    _ = AddIcon();
                }

                return IntPtr.Zero;
        }

        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            _iconAdded = false;
            if (_enabled)
            {
                AddIcon();
            }

            return IntPtr.Zero;
        }

        return NativeShellMethods.DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu()
    {
        var state = _stateProvider();
        var russian = !string.Equals(state.Language, "en", StringComparison.OrdinalIgnoreCase);
        var menu = NativeShellMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var restoreWindowCommands = new Dictionary<uint, TrayAppRecoveryRecord>();
            Append(menu, MenuOpen, russian ? "Открыть TrayX" : "Open TrayX");
            Append(menu, MenuQuick, russian ? "Быстрый доступ" : "Quick panel");
            Append(menu, MenuAudio, russian ? "Микшер" : "Audio mixer");
            _ = NativeShellMethods.AppendMenu(menu, MfSeparator, 0, null);
            Append(
                menu,
                MenuMute,
                state.Muted
                    ? (russian ? "Включить звук" : "Unmute")
                    : (russian ? "Выключить звук" : "Mute"));
            Append(
                menu,
                MenuTheme,
                russian
                    ? (state.DarkTheme ? "Светлая тема Windows" : "Тёмная тема Windows")
                    : (state.DarkTheme ? "Light Windows theme" : "Dark Windows theme"));
            _ = NativeShellMethods.AppendMenu(
                menu,
                MfString | (state.OverflowOpen ? MfChecked : 0),
                MenuOverflow,
                russian ? "Область скрытых значков" : "Hidden icons area");
            Append(menu, MenuTrayApps, russian ? "Приложения в трей…" : "Tray apps…");
            Append(menu, MenuRestoreTrayApps, russian ? "Восстановить скрытые окна" : "Restore hidden windows");
            AppendRestoreWindowMenu(menu, state.PendingTrayApps, russian, restoreWindowCommands);
            _ = NativeShellMethods.AppendMenu(menu, MfSeparator, 0, null);
            Append(menu, MenuSettings, russian ? "Настройки" : "Settings");
            Append(menu, MenuExit, russian ? "Выход" : "Exit");

            _ = NativeShellMethods.SetMenuDefaultItem(menu, MenuOpen, 0);
            _ = NativeShellMethods.SetForegroundWindow(_window);

            if (!NativeShellMethods.GetCursorPos(out var point))
            {
                point = new NativeShellMethods.Point { X = 0, Y = 0 };
            }

            var command = NativeShellMethods.TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCmd | TpmNoNotify,
                point.X,
                point.Y,
                0,
                _window,
                IntPtr.Zero);

            _ = NativeShellMethods.PostMessage(_window, WmNull, IntPtr.Zero, IntPtr.Zero);

            if (command != 0)
            {
                ExecuteMenuCommand(command, restoreWindowCommands);
            }
        }
        finally
        {
            _ = NativeShellMethods.DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, uint id, string text)
    {
        _ = NativeShellMethods.AppendMenu(menu, MfString, id, text);
    }

    private void AppendRestoreWindowMenu(
        IntPtr menu,
        IReadOnlyList<TrayAppRecoveryRecord> pending,
        bool russian,
        IDictionary<uint, TrayAppRecoveryRecord> commands)
    {
        var submenu = NativeShellMethods.CreatePopupMenu();
        if (submenu == IntPtr.Zero)
        {
            _log("Could not create the hidden-window recovery submenu.");
            return;
        }

        var ordered = pending
            .OrderByDescending(item => item.PreparedAtUtc)
            .Take(MaxRestoreWindowMenuItems)
            .ToArray();
        var count = ordered.Length;
        for (var index = 0; index < count; index++)
        {
            var record = ordered[index];
            var command = MenuRestoreWindowBase + (uint)index;
            var title = string.IsNullOrWhiteSpace(record.WindowTitle)
                ? record.WindowClass
                : record.WindowTitle;
            var label = title.Length > 72 ? title[..69] + "..." : title;
            if (!NativeShellMethods.AppendMenu(submenu, MfString, command, label))
            {
                _ = NativeShellMethods.DestroyMenu(submenu);
                _log("Could not add an entry to the hidden-window recovery submenu.");
                return;
            }

            commands.Add(command, record);
        }

        if (pending.Count > count)
        {
            var remaining = pending.Count - count;
            _ = NativeShellMethods.AppendMenu(
                submenu,
                MfString | MfGrayed,
                0,
                russian ? $"И ещё записей: {remaining}" : $"And {remaining} more");
        }

        if (!NativeShellMethods.AppendMenuPopup(
                menu,
                MfPopup | MfString | (pending.Count == 0 ? MfGrayed : 0),
                submenu,
                russian ? "Восстановить одно окно" : "Restore one window"))
        {
            _ = NativeShellMethods.DestroyMenu(submenu);
            _log("Could not attach the hidden-window recovery submenu.");
        }
    }

    private void ExecuteMenuCommand(
        uint command,
        IReadOnlyDictionary<uint, TrayAppRecoveryRecord> restoreWindowCommands)
    {
        if (restoreWindowCommands.TryGetValue(command, out var record))
        {
            InvokeRecovery(record);
            return;
        }

        var mapped = command switch
        {
            MenuOpen => TrayMenuCommand.Open,
            MenuQuick => TrayMenuCommand.QuickPanel,
            MenuAudio => TrayMenuCommand.Audio,
            MenuMute => TrayMenuCommand.ToggleMute,
            MenuTheme => TrayMenuCommand.ToggleTheme,
            MenuOverflow => TrayMenuCommand.ToggleOverflow,
            MenuTrayApps => TrayMenuCommand.TrayApps,
            MenuRestoreTrayApps => TrayMenuCommand.RestoreTrayApps,
            MenuSettings => TrayMenuCommand.Settings,
            MenuExit => TrayMenuCommand.Exit,
            _ => (TrayMenuCommand?)null
        };

        if (mapped is not null)
        {
            InvokeCommand(mapped.Value);
        }
    }

    private void InvokeCommand(TrayMenuCommand command)
    {
        // Меню и события окна не должны блокироваться системными вызовами.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _commandHandler(command);
            }
            catch (Exception exception)
            {
                _log($"Tray command {command} failed: {exception}");
            }
        });
    }

    private void InvokeRecovery(TrayAppRecoveryRecord record)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _restoreWindowHandler(record);
            }
            catch (Exception exception)
            {
                _log($"Tray window recovery failed: {exception}");
            }
        });
    }

    private void InvokeLeftClick()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _leftClickHandler();
            }
            catch (Exception exception)
            {
                _log($"Tray click failed: {exception}");
            }
        });
    }

    private void Cleanup()
    {
        StopIconRetry();

        if (_iconAdded && _window != IntPtr.Zero)
        {
            var data = CreateIconData();
            _ = NativeShellMethods.ShellNotifyIcon(NimDelete, ref data);
            _iconAdded = false;
        }

        if (_window != IntPtr.Zero)
        {
            _ = NativeShellMethods.DestroyWindow(_window);
            _window = IntPtr.Zero;
        }

        if (_windowClass != 0)
        {
            _ = NativeShellMethods.UnregisterClass(_windowClassName, NativeShellMethods.GetModuleHandle(null));
            _windowClass = 0;
        }

        if (_ownsIcon && _icon != IntPtr.Zero)
        {
            _ = NativeShellMethods.DestroyIcon(_icon);
        }

        _icon = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var window = _window;
        if (window != IntPtr.Zero)
        {
            _ = NativeShellMethods.PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        if (_thread is not null && _thread.IsAlive)
        {
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Поток значка TrayX не завершился после остановки.");
            }
        }

        _ready.Dispose();
    }
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct NotifyIconData
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uID;
    public uint uFlags;
    public uint uCallbackMessage;
    public IntPtr hIcon;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
    public string szTip;

    public uint dwState;
    public uint dwStateMask;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string szInfo;

    public uint uTimeoutOrVersion;

    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
    public string szInfoTitle;

    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

internal static class NativeShellMethods
{
    internal delegate IntPtr WndProcDelegate(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    public static ushort RegisterClass(IntPtr instance, string className, WndProcDelegate windowProc)
    {
        var windowClass = new WindowClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WindowClassEx>(),
            style = 0,
            lpfnWndProc = windowProc,
            hInstance = instance,
            lpszClassName = className
        };

        return RegisterClassEx(ref windowClass);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameters);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern UIntPtr SetTimer(IntPtr window, UIntPtr timerId, uint interval, IntPtr timerProcedure);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr window, UIntPtr timerId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenuPopup(IntPtr menu, uint flags, IntPtr submenu, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint TrackPopupMenu(
        IntPtr menu,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr window,
        IntPtr rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadImage(
        IntPtr instance,
        string name,
        uint type,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? name);
}
