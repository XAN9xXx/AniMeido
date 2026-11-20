using Microsoft.UI.Xaml;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace AniMeido.Plugin.Player.Views;

internal static class PlayerNativeWindow
{
    public static nint GetHandle(Window window)
    {
        if (WindowHelper.GetNativeWindow(window) is Win32NativeWindow { Hwnd: not 0 } native)
            return native.Hwnd;
        throw new InvalidOperationException("播放器窗口的 Windows 句柄尚未就绪。");
    }
}
