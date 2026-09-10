namespace AniMeido.Contracts.Playback;

/// <summary>
/// Source-neutral snapshot of the currently active anime playback session.
/// </summary>
public sealed record ActiveAnimePlaybackContext(
    int AnimeId,
    string Title,
    int? EpisodeNumber,
    double? PositionSeconds,
    DateTimeOffset ObservedAt);

public interface IActiveAnimePlaybackContextProvider
{
    Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(
        CancellationToken cancellationToken = default);
}
