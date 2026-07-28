namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// A media URL and request context that libmpv can consume.
/// </summary>
public sealed record ResolvedMedia(
    Uri Uri,
    string DisplayName,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<OnlineSubtitle>? Subtitles = null);
