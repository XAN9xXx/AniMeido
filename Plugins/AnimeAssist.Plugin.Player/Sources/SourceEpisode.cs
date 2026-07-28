namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Episode identity returned by an online source before its media URL is resolved.
/// </summary>
public sealed record SourceEpisode(
    string SourceId,
    string EpisodeId,
    string Title,
    string? Route = null,
    IReadOnlyDictionary<string, string>? Data = null);
