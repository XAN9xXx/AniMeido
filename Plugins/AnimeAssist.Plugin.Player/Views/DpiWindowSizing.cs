using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace AniMeido.Plugin.Player.Views;

internal static class DpiWindowSizing
{
    public static void Resize(Window window, double width, double height)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        var requested = ToPhysicalSize(
            handle,
            width,
            height);
        var workArea = DisplayArea.GetFromWindowId(
            windowId,
            DisplayAreaFallback.Nearest).WorkArea;
        var scale = GetScale(handle);
        var margin = Math.Max(24, (int)Math.Round(48 * scale));
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(
            Math.Min(requested.Width, Math.Max(1, workArea.Width - margin)),
            Math.Min(requested.Height, Math.Max(1, workArea.Height - margin))));
    }

    public static SizeInt32 ToLogicalSize(Window window, SizeInt32 physicalSize)
    {
        var handle = WindowNative.GetWindowHandle(window);
        var scale = GetScale(handle);
        return new SizeInt32(
            Math.Max(1, (int)Math.Round(physicalSize.Width / scale)),
            Math.Max(1, (int)Math.Round(physicalSize.Height / scale)));
    }

    private static SizeInt32 ToPhysicalSize(
        nint handle,
        double width,
        double height)
    {
        var scale = GetScale(handle);
        return new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static double GetScale(nint handle)
    {
        var dpi = GetDpiForWindow(handle);
        return (dpi == 0 ? 96 : dpi) / 96d;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);
}
