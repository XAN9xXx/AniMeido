namespace AniMeido.Plugin.Chat.Models;

internal sealed class ChatRoom(
    string name,
    string description,
    IEnumerable<ChatMessage> messages)
{
    public string Name { get; } = name;

    public string Description { get; } = description;

    public List<ChatMessage> Messages { get; } = [.. messages];
}
