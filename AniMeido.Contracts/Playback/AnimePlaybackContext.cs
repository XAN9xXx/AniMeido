namespace AniMeido.Contracts.Playback;

/// <summary>
/// Identifies the anime that an optional playback plugin should open.
/// The context intentionally contains no source, episode, or player details.
/// </summary>
public sealed record AnimePlaybackContext(
    int AnimeId,
    string Title,
    IReadOnlyList<string>? AlternateTitles = null);
