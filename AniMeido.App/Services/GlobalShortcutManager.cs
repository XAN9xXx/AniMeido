using AniMeido.Contracts.Desktop;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;

namespace AniMeido.App.Services;

public sealed class GlobalShortcutManager : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const uint WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x00000010;
    private readonly IReadOnlyList<IGlobalShortcutAction> _actions;
    private readonly ILogger<GlobalShortcutManager> _logger;
    private readonly LowLevelKeyboardProc _callback;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ShortcutInputGate _inputGate = new();
    private Thread? _thread;
    private nint _hook;
    private uint _threadId;
    private Task? _runningAction;
    private bool _disposed;

    public GlobalShortcutManager(
        IEnumerable<IGlobalShortcutAction> actions,
        ILogger<GlobalShortcutManager> logger)
    {
        _actions = actions.ToList();
        _logger = logger;
        _callback = OnKeyboardInput;
    }

    public async Task StartAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_thread is not null || _actions.Count == 0)
        {
            return;
        }

        foreach (var action in _actions)
        {
            await action.InitializeAsync(cancellationToken);
        }

        _thread = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "AniMeido global shortcut listener",
        };
        _thread.Start();
    }

    private void RunMessageLoop()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(
            WhKeyboardLl,
            _callback,
            GetModuleHandle(null),
            0);
        if (_hook == 0)
        {
            _logger.LogWarning(
                "Unable to install global shortcut hook. Win32={Error}.",
                Marshal.GetLastWin32Error());
            return;
        }

        try
        {
            while (GetMessage(out _, 0, 0, 0) > 0
                && !_shutdown.IsCancellationRequested)
            {
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = 0;
        }
    }

    private nint OnKeyboardInput(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        if ((data.Flags & LlkhfInjected) != 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var action = _actions.FirstOrDefault(item =>
            item.VirtualKey == (int)data.VirtualKey);
        if (action is null)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var message = unchecked((int)wParam);
        if (message is WmKeyUp or WmSysKeyUp)
        {
            _inputGate.ReleaseKey();
            return action.IsEnabled && action.SuppressInput
                ? 1
                : CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (!action.IsEnabled)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        if (message is WmKeyDown or WmSysKeyDown)
        {
            if (_inputGate.TryBegin())
            {
                _runningAction = Task.Run(async () =>
                {
                    try
                    {
                        await action.ExecuteAsync(_shutdown.Token);
                    }
                    catch (OperationCanceledException)
                        when (_shutdown.IsCancellationRequested)
                    {
                    }
#pragma warning disable CA1031 // A hook callback action must never terminate the listener process.
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "Global shortcut action {ActionId} failed.",
                            action.Id);
                    }
#pragma warning restore CA1031
                    finally
                    {
                        _inputGate.CompleteAction();
                    }
                });
            }
        }
        return action.SuppressInput
            ? 1
            : CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, 0, 0);
        }
        _thread?.Join(TimeSpan.FromSeconds(2));
        try
        {
            _runningAction?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
            when (ex.InnerExceptions.All(
                error => error is OperationCanceledException))
        {
        }
        _shutdown.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly nuint ExtraInfo;
    }

    private delegate nint LowLevelKeyboardProc(
        int code,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        LowLevelKeyboardProc callback,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(
        nint hook,
        int code,
        nuint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeMessage
    {
        public readonly nint HWnd;
        public readonly uint Message;
        public readonly nuint WParam;
        public readonly nint LParam;
        public readonly uint Time;
        public readonly int PointX;
        public readonly int PointY;
    }
}
