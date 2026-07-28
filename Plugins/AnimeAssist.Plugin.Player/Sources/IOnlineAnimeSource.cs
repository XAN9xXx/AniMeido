using AniMeido.Contracts.Playback;

namespace AniMeido.Plugin.Player.Sources;

/// <summary>
/// Player-owned extension point implemented by online source adapters.
/// </summary>
public interface IOnlineAnimeSource
{
    string Id { get; }

    string DisplayName { get; }

    Task<IReadOnlyList<SourceEpisode>> GetEpisodesAsync(
        AnimePlaybackContext anime,
        CancellationToken cancellationToken);

    Task<ResolvedMedia> ResolveAsync(
        SourceEpisode episode,
        CancellationToken cancellationToken);
}
