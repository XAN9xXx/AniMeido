using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace AniMeido.Plugin.Player.Views;

internal static class DpiWindowSizing
{
    public static void Resize(Window window, double width, double height)
    {
        var handle = PlayerNativeWindow.GetHandle(window);
        var appWindow = window.AppWindow;
        var requested = ToPhysicalSize(
            handle,
            width,
            height);
        var scale = GetScale(handle);
        var margin = Math.Max(24, (int)Math.Round(48 * scale));
        var monitor = MonitorFromWindow(handle, 2); // MONITOR_DEFAULTTONEAREST
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            appWindow.Resize(requested);
            return;
        }
        var workWidth = monitorInfo.Work.Right - monitorInfo.Work.Left;
        var workHeight = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
        appWindow.Resize(new SizeInt32(
            Math.Min(requested.Width, Math.Max(1, workWidth - margin)),
            Math.Min(requested.Height, Math.Max(1, workHeight - margin))));
    }

    public static SizeInt32 ToLogicalSize(Window window, SizeInt32 physicalSize)
    {
        var handle = PlayerNativeWindow.GetHandle(window);
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
