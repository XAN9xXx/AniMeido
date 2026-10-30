using Microsoft.UI.Dispatching;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AniMeido.App.Services;

public sealed class TrayIconService : IAsyncDisposable
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
    private const uint MsgfltAllow = 1;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<TrayIconService> _logger;
    private readonly WindowProcedure _windowProcedure;
    private readonly object _sync = new();
    private TrayIconSession? _session;
    private Thread? _thread;
    private nint _window;
    private uint _threadId;
    private uint _taskbarCreatedMessage;
    private NotifyIconData _iconData;
    private bool _disposed;

    public TrayIconService(ILogger<TrayIconService> logger)
    {
        _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _windowProcedure = WindowProc;
    }

    public bool IsRunning
    {
        get
        {
            lock (_sync)
                return _thread is not null && _session?.IsRunning == true;
        }
    }

    public Task StartAsync(Action open, Action exit)
        => StartAsync(open, exit, static () => { });

    internal async Task StartAsync(Action open, Action exit, Action unavailable)
    {
        TrayIconSession session;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is not null && _session is { } running)
            {
                if (running.StopRequested)
                {
                    throw new InvalidOperationException("托盘图标仍在关闭，请稍后重试。");
                }
                session = running;
            }
            else
            {
                session = new TrayIconSession(open, exit, unavailable);
                _session = session;
                _thread = new Thread(() => Run(session))
                {
                    IsBackground = true,
                    Name = "AniMeido tray icon",
                };
                _thread.Start();
            }
        }

        try
        {
            await session.Ready.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, session))
                {
                    RequestStop();
                }
            }
            throw new TimeoutException("创建托盘图标超时。");
        }
    }

    private void Run(TrayIconSession session)
    {
        try
        {
            RunCore(session);
        }
#pragma warning disable CA1031 // 原生线程故障交给本轮所有者，清理后再区分启动失败和运行失效。
        catch (Exception ex)
        {
            session.RecordFailure(ex);
        }
#pragma warning restore CA1031
        finally
        {
            bool unavailable;
            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                    _threadId = 0;
                    _window = 0;
                }
                unavailable = session.Complete();
            }
            if (unavailable)
            {
                _logger.LogWarning(session.Failure, "Tray icon thread stopped unexpectedly.");
                Enqueue(session, session.NotifyUnavailable);
            }
        }
    }

    private void RunCore(TrayIconSession session)
    {
        _threadId = GetCurrentThreadId();
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreatedMessage == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册任务栏恢复消息。");
        }
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
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "无法注册托盘窗口类。");
        }

        nint icon = 0;
        var iconAdded = false;
        try
        {
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
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法创建托盘消息窗口。");
            }

            // 只放行本窗口的任务栏恢复消息，管理员运行也能接收普通 Explorer 的广播。
            if (!ChangeWindowMessageFilterEx(_window, _taskbarCreatedMessage, MsgfltAllow, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法接收任务栏恢复消息。");
            }

            icon = LoadIcon(0, new nint(32512));
            if (icon == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "无法加载托盘图标。");
            }

            var data = CreateIconData(_window, icon);
            _iconData = data;
            if (!ShellNotifyIcon(NimAdd, ref data))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows 拒绝添加托盘图标。");
            }

            iconAdded = true;
            session.MarkReady();
            while (session.IsRunning)
            {
                var result = GetMessage(out var message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result < 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "托盘消息循环读取失败。");
                }

                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (iconAdded)
            {
                var data = _iconData;
                _ = ShellNotifyIcon(NimDelete, ref data);
            }

            if (_window != 0)
            {
                DestroyWindow(_window);
                _window = 0;
            }

            _iconData = default;

            _ = UnregisterClass(className, windowClass.Instance);
        }
    }

    private nint WindowProc(
        nint window,
        uint message,
        nuint wParam,
        nint lParam)
    {
        var session = _session;
        if (_taskbarCreatedMessage != 0 && message == _taskbarCreatedMessage)
        {
            if (window == _window && session is not null && session.IsRunning)
                HandleTaskbarCreated(session);

            return 0;
        }

        if (session is null)
            return DefWindowProc(window, message, wParam, lParam);

        if (message == WmTrayCallback)
        {
            var mouseMessage = unchecked((uint)lParam);
            if (mouseMessage == WmLButtonDoubleClick)
            {
                Enqueue(session, session.InvokeOpen);
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
            Enqueue(session, command == OpenCommand ? session.InvokeOpen : session.InvokeExit);
            return 0;
        }
        else if (message == WmDestroy)
        {
            PostQuitMessage(0);
            return 0;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private void HandleTaskbarCreated(TrayIconSession session)
    {
        try
        {
            var data = _iconData;
            if (!ShellNotifyIcon(NimAdd, ref data))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复托盘图标。");
        }
#pragma warning disable CA1031 // 原生回调不抛出故障；消息线程退出后统一通知应用恢复窗口。
        catch (Exception ex)
        {
            session.RecordFailure(ex);
            PostQuitMessage(0);
        }
#pragma warning restore CA1031
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

    private bool IsCurrentSession(TrayIconSession session)
    {
        lock (_sync)
            return !_disposed && ReferenceEquals(_session, session);
    }

    private void Enqueue(TrayIconSession session, Action action)
    {
        if (!IsCurrentSession(session))
            return;

        _dispatcher.TryEnqueue(() =>
        {
            if (IsCurrentSession(session))
            {
                action();
            }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            _disposed = true;
            RequestStop();
            return new ValueTask(_session?.Stopped ?? Task.CompletedTask);
        }
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            RequestStop();
            return _session?.Stopped ?? Task.CompletedTask;
        }
    }

    private void RequestStop()
    {
        _session?.RequestStop();
        if (_window != 0)
        {
            PostMessage(_window, WmClose, 0, 0);
        }
        else if (_threadId != 0)
        {
            PostThreadMessage(_threadId, 0x0012, 0, 0);
        }
    }

    private static NotifyIconData CreateIconData(nint window, nint icon)
    {
        var tip = "AniMeido" + AppDataRoots.DisplaySuffix;
        return new()
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = window,
            Id = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = WmTrayCallback,
            Icon = icon,
            Tip = tip.Length > 127 ? tip[..127] : tip,
        };
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string messageName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        nint window, uint message, uint action, nint changeFilter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(
        string className,
        nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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

    [DllImport("user32.dll", SetLastError = true)]
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadIcon(nint instance, nint iconName);

    [DllImport(
        "shell32.dll",
        EntryPoint = "Shell_NotifyIconW",
        ExactSpelling = true,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
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
