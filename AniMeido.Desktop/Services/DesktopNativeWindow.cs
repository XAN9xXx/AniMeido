#if UNO_SKIA
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace AniMeido.App.Services;

internal static class DesktopNativeWindow
{
    internal static Task OpenFolderAsync(string path)
        => Task.Run(() => Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        })?.Dispose());

    internal static nint GetHandle(Window window)
        => WindowHelper.GetNativeWindow(window) is Win32NativeWindow native && native.Hwnd != 0
            ? native.Hwnd
            : throw new InvalidOperationException("主窗口的 Windows 句柄尚未就绪。");

    internal static void Hide(nint handle)
    {
        // Uno 7.0 的 AppWindow.Hide 尚未实现，托盘驻留直接隐藏现有 HWND。
        if (handle != 0)
            _ = ShowWindow(handle, 0);
    }

    internal static void Show(nint handle)
    {
        if (handle != 0)
            _ = ShowWindow(handle, IsIconic(handle) ? 9 : 5);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint handle);
}
#endif
