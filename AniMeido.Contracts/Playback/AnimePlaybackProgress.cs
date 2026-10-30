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
/// 可选播放器上报播放事实的能力。允许多个线程并发调用；接收端按接纳顺序赋序并入队。
/// 确认某批上报时，不得丢弃尚未交付的事件。
/// </summary>
public interface IAnimePlaybackProgressReporter
{
    Task ReportAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default);
}
