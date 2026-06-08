namespace AniMeido.Plugin.Chat.Models;

/// <summary>
/// 单条聊天消息。
/// </summary>
public sealed class ChatMessage
{
    public int MessageId { get; init; }
    public int RoomId { get; init; }
    public string SenderName { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public bool IsOwnMessage { get; init; }
}
