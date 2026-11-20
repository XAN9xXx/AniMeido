#if UNO_SKIA
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Uno.UI;
using Uno.UI.NativeElementHosting;
using Uno.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;

namespace AniMeido.Plugin.Base.Services;

internal static class DesktopShellServices
{
    private const uint MonitorDefaultToNearest = 2;

    internal static nint GetWindowHandle(Window window)
        => WindowHelper.GetNativeWindow(window) is Win32NativeWindow native && native.Hwnd != 0
            ? native.Hwnd
            : throw new InvalidOperationException("Windows 窗口尚未就绪。");

    internal static RectInt32 GetWorkArea(nint handle)
    {
        if (TryGetWorkArea(MonitorFromWindow(handle, MonitorDefaultToNearest), out var area))
            return area;
        throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    internal static bool TryGetWorkAreaAtClientPoint(
        XamlRoot root, Point origin, out PointInt32 screenPoint, out RectInt32 area)
    {
        screenPoint = default;
        area = default;
        var window = ApplicationHelper.Windows.FirstOrDefault(
            candidate => candidate.Content?.XamlRoot == root);
        if (window is null)
            return false;
        var point = new NativePoint
        {
            X = (int)Math.Round(origin.X * root.RasterizationScale),
            Y = (int)Math.Round(origin.Y * root.RasterizationScale),
        };
        if (!ClientToScreen(GetWindowHandle(window), ref point))
            return false;
        screenPoint = new PointInt32(point.X, point.Y);
        return TryGetWorkArea(MonitorFromPoint(point, MonitorDefaultToNearest), out area);
    }

    internal static Task OpenPath(string path)
        => Task.Run(() => Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        })?.Dispose());

    private static bool TryGetWorkArea(nint monitor, out RectInt32 area)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor != 0 && GetMonitorInfo(monitor, ref info))
        {
            var work = info.Work;
            area = new RectInt32(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            return true;
        }
        area = default;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref NativePoint point);
}
#endif
