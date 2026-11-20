using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
#if !UNO_SKIA
using WinRT.Interop;
#endif

namespace AniMeido.App.Services;

public static class TitleBarHelper
{
    /// <summary>设置 Alt+Tab 窗口图标。</summary>
    public static void SetWindowIcon(Window window)
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (!System.IO.File.Exists(iconPath)) return;

#if UNO_SKIA
        window.AppWindow.SetIcon(iconPath);
#else
        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        AppWindow.GetFromWindowId(windowId).SetIcon(iconPath);
#endif
    }

    /// <summary>
    /// 系统标题栏按钮改用高 48 的样式，与自定义标题栏（含返回按钮）等高。
    /// 需在 ExtendsContentIntoTitleBar 之后调用。
    /// </summary>
    public static void UseTallTitleBar(Window window)
    {
#if UNO_SKIA
        window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
#else
        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        AppWindow.GetFromWindowId(windowId).TitleBar.PreferredHeightOption =
            TitleBarHeightOption.Tall;
#endif
    }

    public static void UpdateButtonColors(AppWindow appWindow, ElementTheme theme)
    {
        var titleBar = appWindow.TitleBar;
        var isLightTheme = theme == ElementTheme.Light ||
            (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Light);

        titleBar.ButtonForegroundColor = isLightTheme ? Colors.Black : Colors.White;
        titleBar.ButtonHoverForegroundColor = isLightTheme ? Colors.Black : Colors.White;
        titleBar.ButtonPressedForegroundColor = isLightTheme ? Colors.Gray : Colors.Gray;
        titleBar.ButtonHoverBackgroundColor = isLightTheme
            ? Color.FromArgb(0x20, 0, 0, 0)
            : Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
        titleBar.ButtonPressedBackgroundColor = isLightTheme
            ? Color.FromArgb(0x30, 0, 0, 0)
            : Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    }
}
