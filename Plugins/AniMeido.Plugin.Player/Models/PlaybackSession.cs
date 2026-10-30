using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Plugin.Player.Models;

internal sealed class PlaybackSession
{
    private int? _episodeNumber;
    private double _position;
    private double _duration;
    private bool _progressReported;

    public AnimePlaybackContext AnimeContext { get; private set; }

    public SourceEpisodeEntry? Episode { get; private set; }

    public ResolvedMedia? Media { get; private set; }

    public PlaybackSession(AnimePlaybackContext animeContext)
    {
        ArgumentNullException.ThrowIfNull(animeContext);
        AnimeContext = animeContext;
    }

    public void ChangeAnime(AnimePlaybackContext animeContext)
    {
        ArgumentNullException.ThrowIfNull(animeContext);
        AnimeContext = animeContext;
        Episode = null;
        Media = null;
        BeginEpisode(null);
    }

    public void BeginEpisode(int? episodeNumber)
    {
        _episodeNumber = episodeNumber;
        _position = 0;
        _duration = 0;
        _progressReported = false;
    }

    public void ObservePlayback(double position, double duration)
    {
        _position = position;
        _duration = duration;
    }

    public AnimePlaybackProgress? TryCreateProgress(bool reachedNaturalEnd)
    {
        if (_progressReported
            || _episodeNumber is not { } episodeNumber
            || !double.IsFinite(_position)
            || !double.IsFinite(_duration)
            || _duration < 300)
            return null;
        if (!reachedNaturalEnd && !(_position / _duration >= 0.9))
            return null;

        // 接纳一次上报后，同集换线路或收到重复结束事件都不再重复上报。
        _progressReported = true;
        return new AnimePlaybackProgress(
            Guid.NewGuid().ToString("N"),
            AnimeContext.AnimeId,
            episodeNumber,
            _position,
            _duration,
            reachedNaturalEnd,
            DateTimeOffset.UtcNow);
    }

    public void SelectEpisode(SourceEpisodeEntry episode)
    {
        ArgumentNullException.ThrowIfNull(episode);
        Episode = episode;
        Media = null;
    }

    public void SetResolvedMedia(ResolvedMedia media)
    {
        ArgumentNullException.ThrowIfNull(media);
        Media = media;
    }

    public void ClearResolvedMedia() => Media = null;
}
