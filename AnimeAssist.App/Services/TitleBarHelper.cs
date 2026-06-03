using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinRT.Interop;

namespace AniMeido.App.Services;

/// <summary>
/// 标题栏辅助：窗口图标设置、标题栏按钮颜色更新。
/// </summary>
public static class TitleBarHelper
{
    /// <summary>设置 Alt+Tab 窗口图标。</summary>
    public static void SetWindowIcon(Window window)
    {
        var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico");
        if (!System.IO.File.Exists(iconPath)) return;

        var hWnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        AppWindow.GetFromWindowId(windowId).SetIcon(iconPath);
    }

    /// <summary>更新标题栏按钮颜色以匹配当前主题。</summary>
    public static void UpdateButtonColors(AppWindow appWindow)
    {
        var titleBar = appWindow.TitleBar;
        var theme = App.ThemeService.GetCurrentTheme();
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
