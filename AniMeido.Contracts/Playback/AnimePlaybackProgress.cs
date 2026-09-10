namespace AniMeido.Contracts.Playback;

/// <summary>
/// A source-neutral playback fact reported by an optional player.
/// </summary>
public sealed record AnimePlaybackProgress(
    string EventId,
    int AnimeId,
    int EpisodeNumber,
    double PositionSeconds,
    double DurationSeconds,
    bool ReachedNaturalEnd,
    DateTimeOffset ObservedAt);

/// <summary>
/// Core capability that accepts playback facts without exposing persistence.
/// </summary>
public interface IAnimePlaybackProgressSink
{
    Task RecordAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional-player capability for reporting source-neutral playback facts.
/// </summary>
public interface IAnimePlaybackProgressReporter
{
    Task ReportAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default);
}
