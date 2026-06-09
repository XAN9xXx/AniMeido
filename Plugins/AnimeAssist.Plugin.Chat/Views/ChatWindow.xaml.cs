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
    // ---- AniMeido 二次元配色 ----
    private static readonly Windows.UI.Color ThemeBg = Windows.UI.Color.FromArgb(255, 22, 22, 40);   // #161628 主背景
    private static readonly Windows.UI.Color ThemeBgPanel = Windows.UI.Color.FromArgb(255, 28, 28, 50);   // #1C1C32 面板背景
    private static readonly Windows.UI.Color ThemeCard = Windows.UI.Color.FromArgb(255, 36, 36, 60);   // #24243C 卡片色
    private static readonly Windows.UI.Color ThemeCardHover = Windows.UI.Color.FromArgb(255, 44, 44, 72);   // #2C2C48 卡片悬停
    private static readonly Windows.UI.Color AccentPurple = Windows.UI.Color.FromArgb(255, 160, 120, 220); // #A078DC 主题紫
    private static readonly Windows.UI.Color AccentSoft = Windows.UI.Color.FromArgb(60, 160, 120, 220); // 半透紫
    private static readonly Windows.UI.Color AccentGlow = Windows.UI.Color.FromArgb(30, 160, 120, 220); // 淡光紫
    private static readonly Windows.UI.Color BubbleOwn = Windows.UI.Color.FromArgb(80, 100, 70, 170); // 自己气泡
    private static readonly Windows.UI.Color BubbleOther = Windows.UI.Color.FromArgb(60, 55, 55, 80);  // 他人气泡
    private static readonly Windows.UI.Color DividerColor = Windows.UI.Color.FromArgb(60, 80, 80, 110); // 分隔线
    private static readonly Windows.UI.Color TextPrimary = Windows.UI.Color.FromArgb(230, 220, 215, 235); // #DCD7EB 主文字
    private static readonly Windows.UI.Color TextSecondary = Windows.UI.Color.FromArgb(160, 150, 145, 175); // 次要文字
    private static readonly Windows.UI.Color TextMuted = Windows.UI.Color.FromArgb(100, 120, 115, 140); // 弱文字
    private static readonly Windows.UI.Color StatusOffline = Windows.UI.Color.FromArgb(200, 100, 100, 120); // 离线红灰
    private static readonly Windows.UI.Color StatusOnline = Windows.UI.Color.FromArgb(200, 100, 200, 140); // 在线绿

    private AppWindow? _appWindow;
    private ToggleSwitch? _alwaysOnTopToggle;
    private ToggleSwitch? _clickThroughToggle;
    private Slider? _opacitySlider;
    private TextBlock? _opacityPercentLabel;
    private TextBlock? _currentRoomLabel;
    private TextBlock? _connectionStatusLabel;
    private TextBlock? _connectionStatusDot;
    private StackPanel? _roomListContent;
    private StackPanel? _messageListContent;
    private TextBox? _messageInput;
    private Button? _sendButton;
    private Button? _imageButton;
    private Button? _fileButton;
    private Button? _settingsButton;
    private Border? _floatingSettingsPanel;
    private IntPtr _windowHandle;
    private readonly ChatWindowSettings _settings = new();
    private readonly ChatViewModel _viewModel = new();
    private bool _settingsPanelVisible;
    private Grid? _currentRoomItem;

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
        _connectionStatusDot = root.FindName("ConnectionStatusDot") as TextBlock;
        _roomListContent = root.FindName("RoomListContent") as StackPanel;
        _messageListContent = root.FindName("MessageListContent") as StackPanel;
        _messageInput = root.FindName("MessageInput") as TextBox;
        _sendButton = root.FindName("SendButton") as Button;
        _imageButton = root.FindName("ImageButton") as Button;
        _fileButton = root.FindName("FileButton") as Button;
        _settingsButton = root.FindName("SettingsButton") as Button;
        _floatingSettingsPanel = root.FindName("FloatingSettingsPanel") as Border;
        _opacityPercentLabel = root.FindName("OpacityPercentLabel") as TextBlock;
        _opacitySlider = root.FindName("OpacitySlider") as Slider;
        _alwaysOnTopToggle = root.FindName("AlwaysOnTopToggle") as ToggleSwitch;
        _clickThroughToggle = root.FindName("ClickThroughToggle") as ToggleSwitch;

        // 事件连接
        if (_sendButton != null)
            _sendButton.Click += OnSendClick;

        if (_imageButton != null)
            _imageButton.Click += OnImageButtonClick;

        if (_fileButton != null)
            _fileButton.Click += OnFileButtonClick;

        if (_messageInput != null)
            _messageInput.KeyDown += OnMessageInputKeyDown;

        if (_settingsButton != null)
            _settingsButton.Click += OnSettingsClick;

        if (_opacitySlider != null)
            _opacitySlider.ValueChanged += OnOpacityValueChanged;

        if (_alwaysOnTopToggle != null)
            _alwaysOnTopToggle.Toggled += OnAlwaysOnTopToggled;

        // 应用主题背景色
        if (root is Panel rootPanel)
            rootPanel.Background = new SolidColorBrush(ThemeBg);
        if (root.FindName("TopBar") is Border topBar)
            topBar.Background = new SolidColorBrush(ThemeBgPanel);
        if (root.FindName("RoomListPanel") is ScrollViewer rlp)
            rlp.Background = new SolidColorBrush(ThemeBgPanel);
        if (root.FindName("MessagePanel") is ScrollViewer mp)
            mp.Background = new SolidColorBrush(ThemeBg);
        if (root.FindName("InputPanel") is Border ip)
            ip.Background = new SolidColorBrush(ThemeBgPanel);
        if (root.FindName("FloatingSettingsPanel") is Border fsp)
            fsp.Background = new SolidColorBrush(ThemeCard);

        // 设置窗口标题
        Title = "AniMeido 小房间";

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

        var emojis = new[] { "💬", "🎬", "🛠" };

        foreach (var room in _viewModel.Rooms)
        {
            var emoji = room.RoomId >= 1 && room.RoomId <= emojis.Length
                ? emojis[room.RoomId - 1] : "💬";

            var emojiBlock = new TextBlock
            {
                Text = emoji,
                FontSize = 24,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Width = 40,
            };

            var nameBlock = new TextBlock
            {
                Text = room.Name,
                FontSize = 14,
                Foreground = new SolidColorBrush(TextPrimary),
            };

            var descBlock = new TextBlock
            {
                Text = room.Description,
                FontSize = 11,
                Foreground = new SolidColorBrush(TextSecondary),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var textStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 2,
                Children = { nameBlock, descBlock },
            };

            var innerGrid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(40) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                },
            };
            innerGrid.Children.Add(emojiBlock);
            Grid.SetColumn(emojiBlock, 0);
            innerGrid.Children.Add(textStack);
            Grid.SetColumn(textStack, 1);

            // 活动指示条
            var indicator = new Border
            {
                Width = 3,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Left,
                CornerRadius = new CornerRadius(0, 2, 2, 0),
                Background = new SolidColorBrush(AccentPurple),
                Visibility = Visibility.Collapsed,
            };

            var card = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(ThemeCard),
                Tag = room,
                Child = new Grid
                {
                    Children =
                    {
                        indicator,
                        innerGrid,
                    },
                },
            };
            card.PointerPressed += OnRoomItemPointerPressed;

            _roomListContent.Children.Add(card);
        }
    }

    private void OnRoomItemPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border border && border.Tag is ChatRoom room)
        {
            SwitchToRoom(room);
        }
    }

    private void SwitchToRoom(ChatRoom room)
    {
        var messages = _viewModel.SwitchToRoom(room);

        if (_currentRoomLabel != null)
            _currentRoomLabel.Text = room.Name;

        // 更新房间卡片高亮
        UpdateRoomHighlight(room);
        BuildMessageList(messages);
    }

    private void UpdateRoomHighlight(ChatRoom activeRoom)
    {
        if (_roomListContent == null) return;

        foreach (var child in _roomListContent.Children)
        {
            if (child is Border card && card.Tag is ChatRoom room)
            {
                // 卡片的 Child 是 Grid，Grid 的第一个子元素是 indicator Border
                var isActive = room.RoomId == activeRoom.RoomId;

                if (card.Child is Grid container && container.Children.Count > 0
                    && container.Children[0] is Border indicator)
                {
                    indicator.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
                }

                card.Background = isActive
                    ? new SolidColorBrush(ThemeCardHover)
                    : new SolidColorBrush(ThemeCard);
            }
        }
    }

    private void BuildMessageList(List<ChatMessage> messages)
    {
        if (_messageListContent == null) return;

        _messageListContent.Children.Clear();

        foreach (var msg in messages)
            AddMessageBubble(msg);
    }

    private void AddMessageBubble(ChatMessage msg)
    {
        if (_messageListContent == null) return;

        var isOwn = msg.IsOwnMessage;

        var senderBlock = new TextBlock
        {
            Text = msg.SenderName,
            FontSize = 11,
            Foreground = new SolidColorBrush(isOwn ? AccentPurple : TextSecondary),
            Margin = new Thickness(0, 0, 0, 3),
        };

        var contentBlock = new TextBlock
        {
            Text = msg.Content,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(TextPrimary),
            LineHeight = 22,
        };

        var innerStack = new StackPanel
        {
            Spacing = 2,
            Children = { senderBlock, contentBlock },
        };

        var bubble = new Border
        {
            Margin = new Thickness(16, 4, 16, 4),
            Padding = new Thickness(14, 8, 14, 10),
            CornerRadius = new CornerRadius(isOwn ? 14 : 14),
            Background = isOwn
                ? new SolidColorBrush(BubbleOwn)
                : new SolidColorBrush(BubbleOther),
            HorizontalAlignment = isOwn
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left,
            MaxWidth = 440,
            Child = innerStack,
        };

        _messageListContent.Children.Add(bubble);
    }

    // ===== 发送逻辑（本地假发送） =====

    private void OnSendClick(object sender, RoutedEventArgs e) => SendMessage();

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
        AddMessageBubble(msg);
    }

    // ===== 图片/文件按钮（占位，暂不实现） =====

    private void OnImageButtonClick(object sender, RoutedEventArgs e)
    {
        // 图片功能待后续实现
    }

    private void OnFileButtonClick(object sender, RoutedEventArgs e)
    {
        // 文件功能待后续实现
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
