using AniMeido.Plugin.Chat.Models;
using AniMeido.Plugin.Chat.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using WinRT.Interop;

namespace AniMeido.Plugin.Chat.Views;

/// <summary>
/// 聊天室独立窗口。提供聊天室 UI 布局，支持置顶切换、Win32 窗口级 alpha 透明度。
/// 该窗口与主窗口处于同一进程，但独立于主窗口 Frame 导航。
///
/// XAML 布局作为本地文件加载（XamlReader.Load），避免 ms-appx:// URI
/// 在动态插件上下文中无法解析的问题。
/// </summary>
public sealed partial class ChatWindow : Window
{
    private AppWindow? _appWindow;
    private ToggleSwitch? _alwaysOnTopToggle;
    private Slider? _opacitySlider;
    private TextBlock? _opacityPercentLabel;
    private IntPtr _windowHandle;
    private readonly ChatWindowSettings _settings = new();

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

        if (root.FindName("AlwaysOnTopToggle") is ToggleSwitch toggle)
        {
            toggle.Toggled += OnAlwaysOnTopToggled;
            _alwaysOnTopToggle = toggle;
        }

        if (root.FindName("OpacitySlider") is Slider slider)
        {
            slider.ValueChanged += OnOpacityValueChanged;
            _opacitySlider = slider;
        }

        _opacityPercentLabel = root.FindName("OpacityPercentLabel") as TextBlock;

        Content = root;
    }

    private void InitializeWindow()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
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

        // 初始化窗口透明度（默认 100%，设置 WS_EX_LAYERED + alpha=255）
        ChatWindowInteropService.SetWindowOpacity(_windowHandle, _settings.WindowOpacityPercent);
    }

    private void OnOpacityValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var percent = (int)e.NewValue;
        _settings.WindowOpacityPercent = percent;

        // 更新百分比标签
        if (_opacityPercentLabel != null)
        {
            _opacityPercentLabel.Text = _settings.WindowOpacityText;
        }

        // 实时更新窗口级透明度
        if (_windowHandle != IntPtr.Zero)
        {
            ChatWindowInteropService.SetWindowOpacity(_windowHandle, _settings.WindowOpacityPercent);
        }

        // 同步 Slider（防止钳位后显示偏差）
        if (_opacitySlider != null && _opacitySlider.Value != _settings.WindowOpacityPercent)
        {
            _opacitySlider.Value = _settings.WindowOpacityPercent;
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
