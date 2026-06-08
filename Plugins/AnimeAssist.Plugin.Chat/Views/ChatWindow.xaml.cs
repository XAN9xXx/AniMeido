using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinRT.Interop;

namespace AniMeido.Plugin.Chat.Views;

/// <summary>
/// 聊天室独立窗口。提供聊天室 UI 布局，支持置顶切换。
/// 该窗口与主窗口处于同一进程，但独立于主窗口 Frame 导航。
///
/// XAML 布局作为本地文件加载（XamlReader.Load），避免 ms-appx:// URI
/// 在动态插件上下文中无法解析的问题。
/// </summary>
public sealed partial class ChatWindow : Window
{
    private AppWindow? _appWindow;
    private ToggleSwitch? _alwaysOnTopToggle;

    /// <summary>默认窗口宽度。</summary>
    private const int DefaultWidth = 1000;

    /// <summary>默认窗口高度。</summary>
    private const int DefaultHeight = 700;

    public ChatWindow()
    {
        LoadXamlLayout();
        InitializeWindow();
    }

    /// <summary>
    /// 从本地文件加载 XAML 布局，连接事件处理。
    /// </summary>
    private void LoadXamlLayout()
    {
        // XAML 文件与插件 DLL 同目录，位于 Views/ 子目录
        var assemblyDir = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var xamlPath = Path.Combine(assemblyDir, "Views", "ChatWindow.xaml");
        var xamlContent = File.ReadAllText(xamlPath);

        if (XamlReader.Load(xamlContent) is not FrameworkElement root)
            return;

        // 连接事件
        if (root.FindName("AlwaysOnTopToggle") is ToggleSwitch toggle)
        {
            toggle.Toggled += OnAlwaysOnTopToggled;
            _alwaysOnTopToggle = toggle;
        }

        Content = root;
    }

    private void InitializeWindow()
    {
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        // 设置初始大小
        _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWidth, DefaultHeight));

        // 居中显示
        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var centerX = (displayArea.WorkArea.Width - DefaultWidth) / 2;
            var centerY = (displayArea.WorkArea.Height - DefaultHeight) / 2;
            _appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
        }
    }

    private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _alwaysOnTopToggle?.IsOn ?? false;
        }
    }
}
