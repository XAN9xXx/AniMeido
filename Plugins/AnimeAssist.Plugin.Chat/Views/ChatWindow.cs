using AniMeido.Plugin.Chat.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace AniMeido.Plugin.Chat.Views;

internal sealed class ChatWindow : Window
{
    private static readonly SolidColorBrush WindowBackground =
        new(ColorHelper.FromArgb(255, 24, 24, 42));
    private static readonly SolidColorBrush PanelBackground =
        new(ColorHelper.FromArgb(255, 31, 31, 53));
    private static readonly SolidColorBrush OwnMessageBackground =
        new(ColorHelper.FromArgb(255, 93, 63, 149));
    private static readonly SolidColorBrush OtherMessageBackground =
        new(ColorHelper.FromArgb(255, 45, 45, 70));

    private readonly ChatSession _session = new();
    private readonly StackPanel _roomList = new() { Spacing = 8 };
    private readonly StackPanel _messageList = new() { Spacing = 10 };
    private readonly TextBlock _roomTitle = new()
    {
        FontSize = 20,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
    };
    private readonly TextBlock _roomDescription = new()
    {
        FontSize = 12,
        Opacity = 0.65,
    };
    private readonly TextBox _messageInput = new()
    {
        PlaceholderText = "输入本地演示消息",
        MinHeight = 42,
    };

    public ChatWindow()
    {
        Title = "AniMeido ChatPlugin";
        Content = BuildLayout();
        ResizeWindow();
        RenderRooms();
        RenderCurrentRoom();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid
        {
            Background = WindowBackground,
            Padding = new Thickness(16),
            RowSpacing = 14,
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid { ColumnSpacing = 12 };
        heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });

        var titlePanel = new StackPanel { Spacing = 3 };
        titlePanel.Children.Add(new TextBlock
        {
            Text = "聊天室",
            FontSize = 26,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = "本地演示 · 未连接服务器 · 关闭后不保存消息",
            FontSize = 12,
            Opacity = 0.65,
        });
        heading.Children.Add(titlePanel);

        var badge = new Border
        {
            Background = OtherMessageBackground,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "可选插件",
                FontSize = 12,
            },
        };
        Grid.SetColumn(badge, 1);
        heading.Children.Add(badge);
        root.Children.Add(heading);

        var content = new Grid { ColumnSpacing = 14 };
        content.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(220) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetRow(content, 1);

        var roomPanel = new Border
        {
            Background = PanelBackground,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Child = _roomList,
        };
        content.Children.Add(roomPanel);

        var conversation = new Grid
        {
            Background = PanelBackground,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(12),
            RowSpacing = 12,
        };
        conversation.RowDefinitions.Add(
            new RowDefinition { Height = GridLength.Auto });
        conversation.RowDefinitions.Add(new RowDefinition());

        var roomHeading = new StackPanel { Spacing = 2 };
        roomHeading.Children.Add(_roomTitle);
        roomHeading.Children.Add(_roomDescription);
        conversation.Children.Add(roomHeading);

        var messages = new ScrollViewer
        {
            Content = _messageList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(messages, 1);
        conversation.Children.Add(messages);
        Grid.SetColumn(conversation, 1);
        content.Children.Add(conversation);
        root.Children.Add(content);

        var composer = new Grid { ColumnSpacing = 10 };
        composer.ColumnDefinitions.Add(new ColumnDefinition());
        composer.ColumnDefinitions.Add(
            new ColumnDefinition { Width = GridLength.Auto });
        composer.Children.Add(_messageInput);

        var sendButton = new Button
        {
            Content = "发送",
            MinWidth = 88,
            MinHeight = 42,
        };
        sendButton.Click += OnSendClick;
        Grid.SetColumn(sendButton, 1);
        composer.Children.Add(sendButton);
        Grid.SetRow(composer, 2);
        root.Children.Add(composer);

        return root;
    }

    private void RenderRooms()
    {
        _roomList.Children.Clear();
        _roomList.Children.Add(new TextBlock
        {
            Text = "本地房间",
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(4, 0, 0, 4),
        });

        foreach (var room in _session.Rooms)
        {
            var button = new Button
            {
                Content = room.Name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Tag = room,
            };
            button.Click += OnRoomClick;
            _roomList.Children.Add(button);
        }
    }

    private void RenderCurrentRoom()
    {
        var room = _session.CurrentRoom;
        _roomTitle.Text = room.Name;
        _roomDescription.Text = room.Description;
        _messageList.Children.Clear();

        foreach (var message in room.Messages)
        {
            var header = $"{message.SenderName}  {message.CreatedAt:HH:mm}";
            var text = new StackPanel { Spacing = 4 };
            text.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 11,
                Opacity = 0.65,
            });
            text.Children.Add(new TextBlock
            {
                Text = message.Content,
                TextWrapping = TextWrapping.Wrap,
            });

            _messageList.Children.Add(new Border
            {
                Background = message.IsOwnMessage
                    ? OwnMessageBackground
                    : OtherMessageBackground,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                HorizontalAlignment = message.IsOwnMessage
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left,
                MaxWidth = 520,
                Child = text,
            });
        }
    }

    private void OnRoomClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ChatRoom room })
        {
            _session.SelectRoom(room);
            RenderCurrentRoom();
        }
    }

    private void OnSendClick(object sender, RoutedEventArgs e)
    {
        if (_session.TrySend(_messageInput.Text))
        {
            _messageInput.Text = string.Empty;
            RenderCurrentRoom();
        }
    }

    private void ResizeWindow()
    {
        var windowHandle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(windowHandle);
        AppWindow.GetFromWindowId(windowId).Resize(new SizeInt32(960, 680));
    }
}
