using Serilog;

namespace AniMeido.App.Services;

/// <summary>
/// 全局未处理异常处理。注册到 AppDomain 和 WinUI。
/// </summary>
internal static class GlobalExceptionHandler
{
    /// <summary>注册三个全局异常处理入口。</summary>
    public static void Register()
    {
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static void OnCurrentDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Fatal(ex, "[AppDomain] 未处理异常");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        var ex = e.Exception?.InnerException ?? e.Exception;
        Log.Error(ex, "[Task] 未观察任务异常");
        e.SetObserved();
    }

    /// <summary>
    /// 致命错误弹窗（使用 Win32 MessageBox，不依赖 WinUI）。
    /// </summary>
    public static void ShowFatalError(string message, IntPtr hWnd)
    {
        Log.Fatal("应用程序将因不可恢复异常退出: {Message}", message);
        Log.CloseAndFlush();
        _ = NativeMethods.MessageBox(hWnd, message, "AniMeido - 不可恢复错误", 0x00000010);
    }

    internal static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        internal static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
    }
}
