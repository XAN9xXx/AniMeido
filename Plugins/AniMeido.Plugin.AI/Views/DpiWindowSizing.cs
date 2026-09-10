using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AniMeido.Plugin.AI.Views;

internal static class DpiWindowSizing
{
    public static void Resize(Window window, double width, double height)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        var requested = new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
        var workArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Nearest).WorkArea;
        var margin = Math.Max(24, (int)Math.Round(48 * scale));
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(
            Math.Min(requested.Width, Math.Max(1, workArea.Width - margin)),
            Math.Min(requested.Height, Math.Max(1, workArea.Height - margin))));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
