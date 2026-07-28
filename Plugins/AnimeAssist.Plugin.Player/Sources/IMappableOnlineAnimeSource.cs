using AniMeido.Contracts.Playback;

namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Optional source capability that separates title matching from episode loading.
/// Existing <see cref="IOnlineAnimeSource"/> implementations remain supported.
/// </summary>
internal interface IMappableOnlineAnimeSource : IOnlineAnimeSource
{
    TimeSpan SearchTimeout => TimeSpan.FromSeconds(15);

    Task<IReadOnlyList<SourceAnimeCandidate>> SearchAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        SourceAnimeCandidate candidate,
        CancellationToken cancellationToken);
}
