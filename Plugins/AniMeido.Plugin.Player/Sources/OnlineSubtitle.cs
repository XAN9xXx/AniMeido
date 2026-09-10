namespace AniMeido.Plugin.Player.Sources;

public sealed record OnlineSubtitle(
    Uri Uri,
    string Title,
    string? Language = null);
