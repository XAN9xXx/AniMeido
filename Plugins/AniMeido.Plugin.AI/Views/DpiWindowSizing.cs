using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;

namespace AniMeido.Plugin.AI.Views;

internal static class DpiWindowSizing
{
    public static void Resize(Window window, double width, double height)
    {
        var handle = WindowHelper.GetNativeWindow(window) is Win32NativeWindow native ? native.Hwnd : 0;
        var dpi = GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        var requested = new SizeInt32(
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
        var monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            window.AppWindow.Resize(requested);
            return;
        }
        var margin = Math.Max(24, (int)Math.Round(48 * scale));
        window.AppWindow.Resize(new SizeInt32(
            Math.Min(requested.Width, Math.Max(1, info.Work.Right - info.Work.Left - margin)),
            Math.Min(requested.Height, Math.Max(1, info.Work.Bottom - info.Work.Top - margin))));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect Work;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
