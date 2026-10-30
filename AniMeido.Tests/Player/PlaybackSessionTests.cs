using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Models;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class PlaybackSessionTests
{
    [Theory]
    [InlineData(269, 300, false, false)]
    [InlineData(270, 300, false, true)]
    [InlineData(0, 300, true, true)]
    [InlineData(299, 299, true, false)]
    [InlineData(0, 0, true, false)]
    [InlineData(0, 300, false, false)]
    public void ProgressUsesTheExistingDurationAndCompletionRules(
        double position, double duration, bool naturalEnd, bool expected)
    {
        var session = new PlaybackSession(new AnimePlaybackContext(42, "Test Anime"));
        session.BeginEpisode(3);
        session.ObservePlayback(position, duration);
        var before = DateTimeOffset.UtcNow;

        var progress = session.TryCreateProgress(naturalEnd);

        Assert.Equal(expected, progress is not null);
        if (progress is null)
            return;

        Assert.Equal(42, progress.AnimeId);
        Assert.Equal(3, progress.EpisodeNumber);
        Assert.Equal(position, progress.PositionSeconds);
        Assert.Equal(duration, progress.DurationSeconds);
        Assert.Equal(naturalEnd, progress.ReachedNaturalEnd);
        Assert.True(Guid.TryParseExact(progress.EventId, "N", out _));
        Assert.InRange(progress.ObservedAt, before, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(double.NaN, 1000, false)]
    [InlineData(double.NaN, 1000, true)]
    [InlineData(double.PositiveInfinity, 1000, false)]
    [InlineData(double.PositiveInfinity, 1000, true)]
    [InlineData(double.NegativeInfinity, 1000, false)]
    [InlineData(double.NegativeInfinity, 1000, true)]
    [InlineData(900, double.NaN, false)]
    [InlineData(900, double.NaN, true)]
    [InlineData(900, double.PositiveInfinity, false)]
    [InlineData(900, double.PositiveInfinity, true)]
    [InlineData(900, double.NegativeInfinity, false)]
    [InlineData(900, double.NegativeInfinity, true)]
    public void NonFiniteSampleDoesNotConsumeTheEpisodeReport(
        double position, double duration, bool naturalEnd)
    {
        var session = new PlaybackSession(new AnimePlaybackContext(42, "Test Anime"));
        session.BeginEpisode(3);
        session.ObservePlayback(position, duration);

        Assert.Null(session.TryCreateProgress(naturalEnd));

        session.ObservePlayback(900, 1000);
        var progress = Assert.IsType<AnimePlaybackProgress>(session.TryCreateProgress(false));

        Assert.Equal(3, progress.EpisodeNumber);
        Assert.Equal(900, progress.PositionSeconds);
        Assert.Equal(1000, progress.DurationSeconds);
        Assert.False(progress.ReachedNaturalEnd);
        Assert.Null(session.TryCreateProgress(reachedNaturalEnd: true));
    }

    [Fact]
    public void UnknownEpisodeNumberDoesNotReportProgress()
    {
        var session = new PlaybackSession(new AnimePlaybackContext(42, "Test Anime"));
        session.BeginEpisode(null);
        session.ObservePlayback(900, 1000);

        Assert.Null(session.TryCreateProgress(reachedNaturalEnd: true));
    }

    [Fact]
    public void RouteChangeKeepsTheEpisodeReportButBeginningAnotherEpisodeResetsIt()
    {
        var session = new PlaybackSession(new AnimePlaybackContext(42, "Test Anime"));
        session.BeginEpisode(1);
        session.ObservePlayback(900, 1000);
        var first = Assert.IsType<AnimePlaybackProgress>(session.TryCreateProgress(false));
        session.SelectEpisode(new SourceEpisodeEntry("Second route",
            new SourceEpisode("test", "episode-1", "Episode 1")));

        Assert.Null(session.TryCreateProgress(reachedNaturalEnd: true));

        session.BeginEpisode(2);
        Assert.Null(session.TryCreateProgress(reachedNaturalEnd: true));
        session.ObservePlayback(1000, 1000);
        var second = Assert.IsType<AnimePlaybackProgress>(session.TryCreateProgress(true));
        Assert.Equal(2, second.EpisodeNumber);
        Assert.NotEqual(first.EventId, second.EventId);
    }

    [Fact]
    public void ChangingAnimeClearsProgressAndEpisodeNumber()
    {
        var session = new PlaybackSession(new AnimePlaybackContext(42, "First"));
        session.BeginEpisode(1);
        session.ObservePlayback(900, 1000);
        Assert.NotNull(session.TryCreateProgress(false));

        session.ChangeAnime(new AnimePlaybackContext(84, "Second"));
        session.ObservePlayback(900, 1000);
        Assert.Null(session.TryCreateProgress(true));
        session.BeginEpisode(4);
        Assert.Null(session.TryCreateProgress(true));
        session.ObservePlayback(900, 1000);
        Assert.Equal(84, session.TryCreateProgress(false)!.AnimeId);
    }

    [Fact]
    public void Constructor_StoresNeutralAnimeContext()
    {
        var context = new AnimePlaybackContext(42, "Test Anime");
        var session = new PlaybackSession(context);

        Assert.Same(context, session.AnimeContext);
    }

    [Fact]
    public void SelectEpisode_ThenResolve_StoresOnlineMedia()
    {
        var session = new PlaybackSession(
            new AnimePlaybackContext(42, "Test Anime"));
        var episode = new SourceEpisodeEntry(
            "Test Source",
            new SourceEpisode("test", "episode-1", "Episode 1"));
        var media = new ResolvedMedia(
            new Uri("https://example.test/episode-1.m3u8"),
            "Episode 1",
            new Dictionary<string, string>());

        session.SelectEpisode(episode);
        session.SetResolvedMedia(media);

        Assert.Same(episode, session.Episode);
        Assert.Same(media, session.Media);
    }

    [Fact]
    public void ChangeAnime_ClearsEpisodeAndMedia()
    {
        var session = new PlaybackSession(
            new AnimePlaybackContext(42, "First"));
        session.SelectEpisode(new SourceEpisodeEntry(
            "Test Source",
            new SourceEpisode("test", "episode-1", "Episode 1")));
        session.SetResolvedMedia(new ResolvedMedia(
            new Uri("https://example.test/episode-1.m3u8"),
            "Episode 1",
            new Dictionary<string, string>()));

        session.ChangeAnime(new AnimePlaybackContext(84, "Second"));

        Assert.Equal(84, session.AnimeContext.AnimeId);
        Assert.Null(session.Episode);
        Assert.Null(session.Media);
    }
}
