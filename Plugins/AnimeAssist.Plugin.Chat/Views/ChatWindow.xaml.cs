using AniMeido.Plugin.Chat.Models;
using AniMeido.Plugin.Chat.Services;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
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
    private ToggleSwitch? _clickThroughToggle;
    private Slider? _opacitySlider;
    private TextBlock? _opacityPercentLabel;
    private TextBlock? _currentRoomLabel;
    private TextBlock? _connectionStatusLabel;
    private StackPanel? _roomListContent;
    private StackPanel? _messageListContent;
    private TextBox? _messageInput;
    private Button? _sendButton;
    private Button? _settingsButton;
    private Grid? _floatingSettingsPanel;
    private IntPtr _windowHandle;
    private readonly ChatWindowSettings _settings = new();
    private readonly ChatViewModel _viewModel = new();
    private bool _settingsPanelVisible;

    /// <summary>默认窗口宽度。</summary>
    private const int DefaultWidth = 1000;

    /// <summary>默认窗口高度。</summary>
    private const int DefaultHeight = 700;

    public ChatWindow()
    {
        LoadXamlLayout();
        InitializeWindow();
        InitializeViewModel();
    }

    /// <summary>
    /// 从本地文件加载 XAML 布局，连接事件处理。
    /// </summary>
    private void LoadXamlLayout()
    {
        var assemblyDir = Path.GetDirectoryName(GetType().Assembly.Location)!;
        var xamlPath = Path.Combine(assemblyDir, "Views", "ChatWindow.xaml");
        var xamlContent = File.ReadAllText(xamlPath);

        if (XamlReader.Load(xamlContent) is not FrameworkElement root)
            return;

        // 区域引用
        _currentRoomLabel = root.FindName("CurrentRoomLabel") as TextBlock;
        _connectionStatusLabel = root.FindName("ConnectionStatusLabel") as TextBlock;
        _roomListContent = root.FindName("RoomListContent") as StackPanel;
        _messageListContent = root.FindName("MessageListContent") as StackPanel;
        _messageInput = root.FindName("MessageInput") as TextBox;
        _sendButton = root.FindName("SendButton") as Button;
        _settingsButton = root.FindName("SettingsButton") as Button;
        _floatingSettingsPanel = root.FindName("FloatingSettingsPanel") as Grid;
        _opacityPercentLabel = root.FindName("OpacityPercentLabel") as TextBlock;
        _opacitySlider = root.FindName("OpacitySlider") as Slider;
        _alwaysOnTopToggle = root.FindName("AlwaysOnTopToggle") as ToggleSwitch;
        _clickThroughToggle = root.FindName("ClickThroughToggle") as ToggleSwitch;

        // 事件连接
        if (_sendButton != null)
            _sendButton.Click += OnSendClick;

        if (_messageInput != null)
            _messageInput.KeyDown += OnMessageInputKeyDown;

        if (_settingsButton != null)
            _settingsButton.Click += OnSettingsClick;

        if (_opacitySlider != null)
            _opacitySlider.ValueChanged += OnOpacityValueChanged;

        if (_alwaysOnTopToggle != null)
            _alwaysOnTopToggle.Toggled += OnAlwaysOnTopToggled;

        Content = root;
    }

    private void InitializeWindow()
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(_windowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        _appWindow.Resize(new Windows.Graphics.SizeInt32(DefaultWidth, DefaultHeight));

        var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
        if (displayArea != null)
        {
            var centerX = (displayArea.WorkArea.Width - DefaultWidth) / 2;
            var centerY = (displayArea.WorkArea.Height - DefaultHeight) / 2;
            _appWindow.Move(new Windows.Graphics.PointInt32(centerX, centerY));
        }

        // 初始化窗口级透明度
        ChatWindowInteropService.SetWindowOpacity(_windowHandle, _settings.WindowOpacityPercent);
    }

    // ===== ViewModel 初始化 =====

    private void InitializeViewModel()
    {
        BuildRoomList();
        SwitchToRoom(_viewModel.CurrentRoom!);
    }

    private void BuildRoomList()
    {
        if (_roomListContent == null) return;

        _roomListContent.Children.Clear();

        foreach (var room in _viewModel.Rooms)
        {
            var item = new Grid
            {
                Height = 48,
                Padding = new Thickness(12, 0, 12, 0),
                Tag = room,
            };
            item.PointerPressed += OnRoomItemPointerPressed;

            var text = new TextBlock
            {
                Text = room.Name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 14,
            };
            item.Children.Add(text);

            _roomListContent.Children.Add(item);
        }
    }

    private void OnRoomItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.Tag is ChatRoom room)
        {
            SwitchToRoom(room);
        }
    }

    private void SwitchToRoom(ChatRoom room)
    {
        var messages = _viewModel.SwitchToRoom(room);

        if (_currentRoomLabel != null)
            _currentRoomLabel.Text = room.Name;

        BuildMessageList(messages);
    }

    private void BuildMessageList(List<ChatMessage> messages)
    {
        if (_messageListContent == null) return;

        _messageListContent.Children.Clear();

        foreach (var msg in messages)
        {
            var border = new Border
            {
                Margin = new Thickness(8, 4, 8, 4),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(8),
                Background = msg.IsOwnMessage
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 212))
                    : new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
                HorizontalAlignment = msg.IsOwnMessage
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                MaxWidth = 400,
            };

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = msg.SenderName,
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 180, 180, 180)),
            });
            stack.Children.Add(new TextBlock
            {
                Text = msg.Content,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
            });

            border.Child = stack;
            _messageListContent.Children.Add(border);
        }
    }

    // ===== 发送逻辑（本地假发送） =====

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        SendMessage();
    }

    private void OnMessageInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
    }

    private void SendMessage()
    {
        if (_messageInput == null) return;

        var text = _messageInput.Text;
        var msg = _viewModel.FakeSend(text);
        if (msg == null) return;

        _messageInput.Text = string.Empty;

        // 追加到消息列表
        var border = new Border
        {
            Margin = new Thickness(8, 4, 8, 4),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 0, 120, 212)),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 400,
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = msg.SenderName,
            FontSize = 11,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(180, 180, 180, 180)),
        });
        stack.Children.Add(new TextBlock
        {
            Text = msg.Content,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        });

        border.Child = stack;
        _messageListContent?.Children.Add(border);
    }

    // ===== 设置面板 =====

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        _settingsPanelVisible = !_settingsPanelVisible;
        if (_floatingSettingsPanel != null)
            _floatingSettingsPanel.Visibility = _settingsPanelVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // ===== 透明度 =====

    private void OnOpacityValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var percent = (int)e.NewValue;
        _settings.WindowOpacityPercent = percent;

        if (_opacityPercentLabel != null)
            _opacityPercentLabel.Text = _settings.WindowOpacityText;

        if (_windowHandle != IntPtr.Zero)
            ChatWindowInteropService.SetWindowOpacity(_windowHandle, _settings.WindowOpacityPercent);

        if (_opacitySlider != null && _opacitySlider.Value != _settings.WindowOpacityPercent)
            _opacitySlider.Value = _settings.WindowOpacityPercent;
    }

    // ===== 置顶 =====

    private void OnAlwaysOnTopToggled(object sender, RoutedEventArgs e)
    {
        if (_appWindow?.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = _alwaysOnTopToggle?.IsOn ?? false;
        }
    }
}
