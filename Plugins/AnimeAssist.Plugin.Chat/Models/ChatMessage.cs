namespace AniMeido.Plugin.Chat.Models;

internal sealed record ChatMessage(
    string SenderName,
    string Content,
    DateTime CreatedAt,
    bool IsOwnMessage);
