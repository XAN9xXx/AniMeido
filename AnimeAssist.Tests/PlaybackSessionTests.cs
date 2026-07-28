using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Models;
using AniMeido.Plugin.Player.Sources;

namespace AniMeido.Tests;

public sealed class PlaybackSessionTests
{
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
