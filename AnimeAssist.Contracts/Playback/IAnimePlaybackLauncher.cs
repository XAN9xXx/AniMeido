namespace AniMeido.Contracts.Playback;

/// <summary>
/// Narrow capability exposed by an optional anime playback plugin.
/// </summary>
public interface IAnimePlaybackLauncher
{
    /// <summary>
    /// Opens the playback experience for the supplied anime context.
    /// </summary>
    Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default);
}
