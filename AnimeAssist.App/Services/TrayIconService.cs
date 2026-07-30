using Microsoft.UI.Dispatching;
using System.Runtime.InteropServices;

namespace AniMeido.App.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint WmTrayCallback = WmApp + 1;
    private const uint WmCommand = 0x0111;
    private const uint WmDestroy = 0x0002;
    private const uint WmClose = 0x0010;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint MfString = 0;
    private const uint TpmRightButton = 2;
    private const int OpenCommand = 1;
    private const int ExitCommand = 2;
    private readonly DispatcherQueue _dispatcher;
    private readonly WindowProcedure _windowProcedure;
    private readonly object _sync = new();
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private nint _window;
    private uint _threadId;
    private Action? _open;
    private Action? _exit;
    private bool _disposed;

    public TrayIconService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _windowProcedure = WindowProc;
    }

    public void Start(Action open, Action exit)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_sync)
        {
            _open = open;
            _exit = exit;
            if (_thread is not null)
            {
                return;
            }

            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "AniMeido tray icon",
            };
            _ready.Reset();
            _thread.Start();
        }
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        var className = $"AniMeido.Tray.{Environment.ProcessId}";
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            WindowProcedure = _windowProcedure,
            Instance = GetModuleHandle(null),
            ClassName = className,
        };
        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            _ready.Set();
            return;
        }

        _window = CreateWindowEx(
            0,
            className,
            "AniMeido",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            windowClass.Instance,
            0);
        if (_window == 0)
        {
            _ready.Set();
            return;
        }

        var icon = LoadIcon(0, new nint(32512));
        var data = CreateIconData(_window, icon);
        _ = ShellNotifyIcon(NimAdd, ref data);
        _ready.Set();
        try
        {
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            data = CreateIconData(_window, icon);
            _ = ShellNotifyIcon(NimDelete, ref data);
            if (_window != 0)
            {
                DestroyWindow(_window);
                _window = 0;
            }

            _ = UnregisterClass(className, windowClass.Instance);
        }
    }

    private nint WindowProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam)
    {
        if (message == WmTrayCallback)
        {
            var mouseMessage = unchecked((uint)lParam);
            if (mouseMessage == WmLButtonDoubleClick)
            {
                Enqueue(_open);
                return 0;
            }

            if (mouseMessage == WmRButtonUp)
            {
                ShowMenu(window);
                return 0;
            }
        }
        else if (message == WmCommand)
        {
            var command = unchecked((int)(wParam & 0xffff));
            Enqueue(command == OpenCommand ? _open : _exit);
            return 0;
        }
        else if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void ShowMenu(nint window)
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenu(menu, MfString, OpenCommand, "打开 AniMeido");
            _ = AppendMenu(menu, MfString, ExitCommand, "退出");
            _ = GetCursorPos(out var point);
            _ = SetForegroundWindow(window);
            _ = TrackPopupMenu(
                menu,
                TpmRightButton,
                point.X,
                point.Y,
                0,
                window,
                0);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void Enqueue(Action? action)
    {
        if (action is not null)
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _ready.Dispose();
    }

    public void Stop()
    {
        if (_window != 0)
        {
            PostMessage(_window, WmClose, 0, 0);
        }
        else if (_threadId != 0)
        {
            PostThreadMessage(_threadId, 0x0012, 0, 0);
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        lock (_sync)
        {
            _thread = null;
            _threadId = 0;
            _window = 0;
        }
    }

    private static NotifyIconData CreateIconData(nint window, nint icon)
        => new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = WmTrayCallback,
            Icon = icon,
            Tip = "AniMeido",
        };

    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid GuidItem;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(
        string className,
        nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        nint window,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint thread,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShellNotifyIcon(
        uint message,
        ref NotifyIconData data);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(
        nint menu,
        uint flags,
        nuint id,
        string text);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint window,
        nint rectangle);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
