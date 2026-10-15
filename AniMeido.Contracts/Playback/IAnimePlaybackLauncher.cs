namespace AniMeido.Contracts.Playback;

/// <summary>
/// Narrow capability exposed by an optional anime playback plugin.
/// </summary>
public interface IAnimePlaybackLauncher
{
    bool IsAvailable { get; }

    event EventHandler? AvailabilityChanged;

    Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default);
}
