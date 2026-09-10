using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using Windows.Foundation;

namespace AniMeido.Plugin.Player.Playback;

/// <summary>
/// Child HWND used as libmpv's Win32 video output target.
/// </summary>
internal sealed class NativeVideoHost : IDisposable
{
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private readonly nint _parentWindow;
    private nint _window;

    public NativeVideoHost(nint parentWindow)
    {
        _parentWindow = parentWindow;
        _window = CreateWindowEx(
            0,
            "STATIC",
            string.Empty,
            WsChild | WsVisible | WsClipSiblings,
            0,
            0,
            1,
            1,
            parentWindow,
            0,
            0,
            0);
        if (_window == 0)
        {
            throw new InvalidOperationException(
                $"无法创建视频渲染窗口，Win32 错误：{Marshal.GetLastWin32Error()}");
        }
    }

    public nint Handle => _window;

    public void UpdateBounds(FrameworkElement element, UIElement root)
    {
        if (_window == 0
            || element.ActualWidth <= 0
            || element.ActualHeight <= 0)
        {
            return;
        }

        Point origin = element
            .TransformToVisual(root)
            .TransformPoint(new Point());
        var dpi = GetDpiForWindow(_parentWindow);
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        _ = SetWindowPos(
            _window,
            0,
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale),
            Math.Max(1, (int)Math.Round(element.ActualWidth * scale)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * scale)),
            SwpNoActivate | SwpShowWindow);
    }

    public void Dispose()
    {
        if (_window == 0)
        {
            return;
        }

        _ = DestroyWindow(_window);
        _window = 0;
    }

    [DllImport(
        "user32.dll",
        EntryPoint = "CreateWindowExW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern nint CreateWindowEx(
        int extendedStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        nint parentWindow,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);
}
