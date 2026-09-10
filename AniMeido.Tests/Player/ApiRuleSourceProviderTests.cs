using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player.Sources.Rules;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

public sealed class ApiRuleSourceProviderTests
{
    [Fact]
    public async Task RuleSource_SearchesEpisodesAndResolvesMedia()
    {
        using var httpClient = new HttpClient(new StubHandler());
        var provider = new ApiRuleSourceProvider(
            httpClient,
            new ApiSourceRule
            {
                Id = "test.api",
                DisplayName = "Test API",
                Search = new ApiSearchRule
                {
                    Url = "https://example.test/search?q={query}",
                    ItemsPath = "data.items",
                    IdPath = "id",
                    TitlePath = "title",
                },
                Episodes = new ApiEpisodesRule
                {
                    Url = "https://example.test/anime/{animeId}/episodes",
                    ItemsPath = "data.episodes",
                    IdPath = "id",
                    TitlePath = "title",
                    RoutePath = "quality",
                },
                Resolve = new ApiResolveRule
                {
                    Url = "https://example.test/play/{episodeId}",
                    MediaUrlPath = "data.url",
                    Headers = new Dictionary<string, string>
                    {
                        ["Referer"] = "https://example.test/",
                    },
                },
            });

        var episodes = await provider.GetEpisodesAsync(
            new AnimePlaybackContext(42, "Test Anime"),
            CancellationToken.None);
        var episode = Assert.Single(episodes);
        var media = await provider.ResolveAsync(
            episode,
            CancellationToken.None);

        Assert.Equal("episode-1", episode.EpisodeId);
        Assert.Equal("1080p", episode.Route);
        Assert.Equal(
            "https://media.example.test/episode-1.m3u8",
            media.Uri.AbsoluteUri);
        Assert.Equal(
            "https://example.test/",
            media.Headers["Referer"]);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            var json = path switch
            {
                "/search" =>
                    """{"data":{"items":[{"id":"anime-1","title":"Test Anime"}]}}""",
                "/anime/anime-1/episodes" =>
                    """{"data":{"episodes":[{"id":"episode-1","title":"Episode 1","quality":"1080p"}]}}""",
                "/play/episode-1" =>
                    """{"data":{"url":"https://media.example.test/episode-1.m3u8"}}""",
                _ => throw new InvalidOperationException($"Unexpected URL: {request.RequestUri}"),
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    json,
                    Encoding.UTF8,
                    "application/json"),
            });
        }
    }
}
