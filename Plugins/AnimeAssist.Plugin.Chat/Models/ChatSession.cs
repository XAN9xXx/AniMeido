namespace AniMeido.Plugin.Chat.Models;

internal sealed class ChatSession
{
    public ChatSession()
    {
        var now = DateTime.Now;
        Rooms =
        [
            new ChatRoom(
                "茶水间",
                "本地演示房间",
                [
                    new ChatMessage("Alice", "今天有人看新番了吗？", now.AddMinutes(-12), false),
                    new ChatMessage("Bob", "看了，这季质量不错。", now.AddMinutes(-9), false),
                ]),
            new ChatRoom(
                "番剧部屋",
                "聊聊最近在看的作品",
                [
                    new ChatMessage("Charlie", "最近有什么轻松的日常番？", now.AddMinutes(-6), false),
                    new ChatMessage("我", "可以试试《摇曳露营》。", now.AddMinutes(-4), true),
                ]),
        ];
        CurrentRoom = Rooms[0];
    }

    public IReadOnlyList<ChatRoom> Rooms { get; }

    public ChatRoom CurrentRoom { get; private set; }

    public void SelectRoom(ChatRoom room)
    {
        ArgumentNullException.ThrowIfNull(room);
        if (!Rooms.Contains(room))
        {
            throw new ArgumentException("房间不属于当前聊天会话。", nameof(room));
        }

        CurrentRoom = room;
    }

    public bool TrySend(string? content)
    {
        var normalized = content?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return false;
        }

        CurrentRoom.Messages.Add(
            new ChatMessage("我", normalized, DateTime.Now, true));
        return true;
    }
}
