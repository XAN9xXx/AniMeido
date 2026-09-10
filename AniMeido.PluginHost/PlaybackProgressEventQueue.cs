using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;

namespace AniMeido.PluginHost;

internal sealed class PlaybackProgressEventQueue :
    IAnimePlaybackProgressReporter
{
    private const int MaximumEventCount = 256;
    private readonly object _syncRoot = new();
    private readonly Queue<HostedPlaybackProgressEvent> _events = new();
    private long _nextSequence;

    public Task ReportAsync(
        AnimePlaybackProgress progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _nextSequence);
        lock (_syncRoot)
        {
            _events.Enqueue(new HostedPlaybackProgressEvent(
                sequence,
                progress.EventId,
                progress.AnimeId,
                progress.EpisodeNumber,
                progress.PositionSeconds,
                progress.DurationSeconds,
                progress.ReachedNaturalEnd,
                progress.ObservedAt));
            while (_events.Count > MaximumEventCount)
            {
                _events.Dequeue();
            }
        }

        return Task.CompletedTask;
    }

    public HostedPlaybackProgressEvent[] GetPendingEvents()
    {
        lock (_syncRoot)
        {
            return _events.ToArray();
        }
    }

    public void Acknowledge(long sequence)
    {
        lock (_syncRoot)
        {
            while (_events.TryPeek(out var item)
                && item.Sequence <= sequence)
            {
                _events.Dequeue();
            }
        }
    }
}
