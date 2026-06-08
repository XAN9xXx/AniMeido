using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Chat.Models;

/// <summary>
/// 聊天室页面 ViewModel。维护房间列表、当前房间、消息列表、输入状态。
/// 当前使用假数据，不接入真实服务器。
/// </summary>
public sealed class ChatViewModel
{
    /// <summary>预设房间列表。</summary>
    public ObservableCollection<ChatRoom> Rooms { get; } = new();

    /// <summary>当前房间消息列表。</summary>
    public ObservableCollection<ChatMessage> CurrentRoomMessages { get; } = new();

    /// <summary>当前选中的房间。</summary>
    public ChatRoom? CurrentRoom { get; set; }

    /// <summary>输入框文字。</summary>
    public string InputText { get; set; } = string.Empty;

    /// <summary>是否为自身发送的消息 ID 前缀，用于区分左右对齐。</summary>
    private int _nextMessageId;

    public ChatViewModel()
    {
        LoadFakeData();
    }

    private void LoadFakeData()
    {
        // 预设房间
        var room1 = new ChatRoom { RoomId = 1, Name = "综合讨论", Description = "大家在聊什么呢" };
        var room2 = new ChatRoom { RoomId = 2, Name = "番剧推荐", Description = "推荐好看的番" };
        var room3 = new ChatRoom { RoomId = 3, Name = "技术讨论", Description = "技术相关的话题" };

        Rooms.Add(room1);
        Rooms.Add(room2);
        Rooms.Add(room3);

        // 默认选中第一个房间
        CurrentRoom = room1;

        // 房间 1 的假消息
        AddFakeMessage(room1.RoomId, "Alice", "今天有人看新番了吗？");
        AddFakeMessage(room1.RoomId, "Bob", "看了，这季质量不错");
        AddFakeMessage(room1.RoomId, "Alice", "确实，比上一季好多了");
        AddOwnFakeMessage(room1.RoomId, "我也觉得，尤其是那个新出的");

        // 房间 2 的假消息
        AddFakeMessage(room2.RoomId, "Charlie", "最近有什么好看的日常番？");
        AddFakeMessage(room2.RoomId, "Dave", "推荐《摇曳露营》");

        // 房间 3 的假消息
        AddFakeMessage(room3.RoomId, "Eve", "有人用 WinUI 3 开发吗？");
        AddFakeMessage(room3.RoomId, "Frank", "我在用，还不错");
    }

    private void AddFakeMessage(int roomId, string sender, string content)
    {
        _nextMessageId++;
        CurrentRoomMessages.Add(new ChatMessage
        {
            MessageId = _nextMessageId,
            RoomId = roomId,
            SenderName = sender,
            Content = content,
            CreatedAt = DateTime.Now,
            IsOwnMessage = false,
        });
    }

    private void AddOwnFakeMessage(int roomId, string content)
    {
        _nextMessageId++;
        CurrentRoomMessages.Add(new ChatMessage
        {
            MessageId = _nextMessageId,
            RoomId = roomId,
            SenderName = "我",
            Content = content,
            CreatedAt = DateTime.Now,
            IsOwnMessage = true,
        });
    }

    /// <summary>
    /// 切换当前房间，加载该房间的消息。
    /// </summary>
    public List<ChatMessage> SwitchToRoom(ChatRoom room)
    {
        CurrentRoom = room;
        return GetRoomMessages(room.RoomId);
    }

    /// <summary>
    /// 本地假发送：向当前房间添加一条自己的消息。
    /// </summary>
    public ChatMessage? FakeSend(string text)
    {
        if (CurrentRoom == null || string.IsNullOrWhiteSpace(text))
            return null;

        _nextMessageId++;
        var msg = new ChatMessage
        {
            MessageId = _nextMessageId,
            RoomId = CurrentRoom.RoomId,
            SenderName = "我",
            Content = text.Trim(),
            CreatedAt = DateTime.Now,
            IsOwnMessage = true,
        };

        return msg;
    }

    private static List<ChatMessage> GetRoomMessages(int roomId)
    {
        // 假数据：为每个房间生成不同的消息
        return roomId switch
        {
            1 => new List<ChatMessage>
            {
                new() { MessageId = 1, RoomId = 1, SenderName = "Alice", Content = "今天有人看新番了吗？", IsOwnMessage = false },
                new() { MessageId = 2, RoomId = 1, SenderName = "Bob", Content = "看了，这季质量不错", IsOwnMessage = false },
                new() { MessageId = 3, RoomId = 1, SenderName = "Alice", Content = "确实，比上一季好多了", IsOwnMessage = false },
                new() { MessageId = 4, RoomId = 1, SenderName = "我", Content = "我也觉得，尤其是那个新出的", IsOwnMessage = true },
            },
            2 => new List<ChatMessage>
            {
                new() { MessageId = 5, RoomId = 2, SenderName = "Charlie", Content = "最近有什么好看的日常番？", IsOwnMessage = false },
                new() { MessageId = 6, RoomId = 2, SenderName = "Dave", Content = "推荐《摇曳露营》", IsOwnMessage = false },
            },
            3 => new List<ChatMessage>
            {
                new() { MessageId = 7, RoomId = 3, SenderName = "Eve", Content = "有人用 WinUI 3 开发吗？", IsOwnMessage = false },
                new() { MessageId = 8, RoomId = 3, SenderName = "Frank", Content = "我在用，还不错", IsOwnMessage = false },
                new() { MessageId = 9, RoomId = 3, SenderName = "Eve", Content = "有什么坑吗？", IsOwnMessage = false },
                new() { MessageId = 10, RoomId = 3, SenderName = "Frank", Content = "动态插件加载的 XAML 解析要注意", IsOwnMessage = false },
            },
            _ => new List<ChatMessage>(),
        };
    }
}
