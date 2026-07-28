using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Rules;

namespace AniMeido.Tests;

public sealed class StaticSourceProviderTests
{
    [Fact]
    public async Task Provider_ReturnsConfiguredEpisodeAndMedia()
    {
        var provider = new StaticSourceProvider(new StaticSourceRule
        {
            Id = "test.static",
            DisplayName = "Static test",
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = "AniMeido-Test",
            },
            Episodes =
            [
                new StaticSourceEpisodeRule
                {
                    Id = "episode-1",
                    Title = "Connectivity test",
                    Route = "HLS",
                    MediaUrl = "https://example.test/test.m3u8",
                    Headers = new Dictionary<string, string>
                    {
                        ["Referer"] = "https://example.test/",
                    },
                },
            ],
        });

        var episodes = await provider.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Any anime"),
            CancellationToken.None);
        var episode = Assert.Single(episodes);
        var media = await provider.ResolveAsync(
            episode,
            CancellationToken.None);

        Assert.Equal("HLS", episode.Route);
        Assert.Equal(
            "https://example.test/test.m3u8",
            media.Uri.AbsoluteUri);
        Assert.Equal("AniMeido-Test", media.Headers["User-Agent"]);
        Assert.Equal(
            "https://example.test/",
            media.Headers["Referer"]);
    }

    [Fact]
    public void Constructor_RejectsDuplicateEpisodeIds()
    {
        var rule = new StaticSourceRule
        {
            Id = "test.static",
            DisplayName = "Static test",
            Episodes =
            [
                CreateEpisode("duplicate"),
                CreateEpisode("duplicate"),
            ],
        };

        Assert.Throws<InvalidDataException>(
            () => new StaticSourceProvider(rule));
    }

    private static StaticSourceEpisodeRule CreateEpisode(string id)
        => new()
        {
            Id = id,
            Title = id,
            MediaUrl = "https://example.test/test.m3u8",
        };
}
