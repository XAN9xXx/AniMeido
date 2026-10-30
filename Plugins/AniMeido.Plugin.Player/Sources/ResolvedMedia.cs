namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// A media URL and request context that libmpv can consume.
/// </summary>
/// <param name="ExpiresAt">源可提供的失效提示；当前播放器不据此自动重新解析，字段保留以兼容代码源。</param>
public sealed record ResolvedMedia(
    Uri Uri,
    string DisplayName,
    IReadOnlyDictionary<string, string> Headers,
    DateTimeOffset? ExpiresAt = null,
    IReadOnlyList<OnlineSubtitle>? Subtitles = null);
