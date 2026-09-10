namespace AniMeido.Contracts.Playback;

/// <summary>
/// Narrow capability exposed by an optional anime playback plugin.
/// </summary>
public interface IAnimePlaybackLauncher
{
    /// <summary>Whether an online playback implementation is currently available.</summary>
    bool IsAvailable { get; }

    /// <summary>Raised when <see cref="IsAvailable"/> changes.</summary>
    event EventHandler? AvailabilityChanged;

    /// <summary>
    /// Opens the playback experience for the supplied anime context.
    /// </summary>
    Task LaunchAsync(
        AnimePlaybackContext context,
        CancellationToken cancellationToken = default);
}
