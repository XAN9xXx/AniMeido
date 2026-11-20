using System.Globalization;
using System.Runtime.InteropServices;

namespace AniMeido.App.Services;

internal static class WindowsNotificationRuntime
{
    public static void Initialize()
    {
        // 与 Windows App SDK 自包含初始化保持一致，使用当前进程的程序目录解析通知组件。
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY", AppContext.BaseDirectory);
        Environment.SetEnvironmentVariable("MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY_PID",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Marshal.ThrowExceptionForHR(WindowsAppRuntime_EnsureIsLoaded());
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int WindowsAppRuntime_EnsureIsLoaded();
}
