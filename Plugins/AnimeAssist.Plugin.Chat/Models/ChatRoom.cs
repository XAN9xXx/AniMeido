namespace AniMeido.Plugin.Chat.Models;

/// <summary>
/// 聊天室房间。
/// </summary>
public sealed class ChatRoom
{
    public int RoomId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
}
