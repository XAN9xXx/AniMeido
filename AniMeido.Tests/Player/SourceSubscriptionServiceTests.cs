using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

public sealed class SourceSubscriptionServiceTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(
        Path.GetTempPath(),
        $"animeido-subscription-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task PreviewAndApply_InstallsEasySourceDisabled()
    {
        var handler = new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                return Json("""
                    {
                      "truncated": false,
                      "tree": [
                        { "path": "inner_source/example.js", "type": "blob" },
                        { "path": "inner_source/block-old.js", "type": "blob" }
                      ]
                    }
                    """);
            }

            var script = request.RequestUri.AbsolutePath.EndsWith(
                "block-old.js",
                StringComparison.Ordinal)
                ? """
                  // @key old
                  // @label Old
                  // @libVersion 15
                  """
                : """
                  // @key example
                  // @label Example
                  // @versionName 1.0
                  // @versionCode 1
                  // @libVersion 15
                  function SearchComponent_search() { return new Pair(null, new ArrayList()); }
                  function DetailedComponent_getDetailed() { return new Pair(null, new ArrayList()); }
                  function PlayComponent_getPlayInfo() { return new PlayerInfo(0, "https://example.test/video.mp4"); }
                  """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(script, Encoding.UTF8),
            };
        });
        using var httpClient = new HttpClient(handler);
        var sourcesDirectory = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sourcesDirectory);
        var reader = new GitHubSourceReader(httpClient);
        var service = new SourceSubscriptionService(
            reader,
            installer,
            Path.Combine(_testDirectory, "subscriptions.json"));

        var preview = await service.PreviewAsync(
            "https://github.com/easybangumiorg/EasyBangumi/tree/main/inner_source",
            CancellationToken.None);

        Assert.Single(preview.Items, item =>
            item.Change == SubscriptionChangeKind.Added);
        Assert.Single(preview.Items, item =>
            item.Change == SubscriptionChangeKind.Skipped);

        await service.ApplyAsync(preview, CancellationToken.None);

        var installed = Assert.Single(await installer.ListAsync(
            CancellationToken.None));
        Assert.Equal("easybangumi.example", installed.Id);
        Assert.Equal("easybangumi-js", installed.SourceKind);
        Assert.False(installed.IsEnabled);
    }

    [Fact]
    public async Task RemoveSubscription_KeepsPackageDisabledAndUnmanaged()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.Host == "api.github.com"
                ? Json("""
                    {
                      "truncated": false,
                      "tree": [
                        { "path": "subs/web/t0/example.json", "type": "blob" }
                      ]
                    }
                    """)
                : Json("""
                    {
                      "factoryId": "web-selector",
                      "version": 2,
                      "arguments": {
                        "name": "Example",
                        "searchConfig": {
                          "searchUrl": "https://example.test/search?q={keyword}",
                          "subjectFormatId": "a",
                          "selectorSubjectFormatA": {
                            "selectLists": "a.result"
                          },
                          "channelFormatId": "no-channel",
                          "selectorChannelFormatNoChannel": {
                            "selectEpisodes": "a.episode"
                          },
                          "onlySupportsPlayers": [],
                          "matchVideo": {
                            "matchVideoUrl": "https://.+\\.m3u8"
                          }
                        }
                      }
                    }
                    """));
        using var httpClient = new HttpClient(handler);
        var sourcesDirectory = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sourcesDirectory);
        var service = new SourceSubscriptionService(
            new GitHubSourceReader(httpClient),
            installer,
            Path.Combine(_testDirectory, "subscriptions.json"));
        var preview = await service.PreviewAsync(
            "https://github.com/creamycake-anime/ani-subs/tree/main",
            CancellationToken.None);
        await service.ApplyAsync(preview, CancellationToken.None);

        await service.RemoveAsync(
            preview.SubscriptionId,
            CancellationToken.None);

        var installed = Assert.Single(await installer.ListAsync(
            CancellationToken.None));
        Assert.False(installed.IsEnabled);
        Assert.True(installed.IsUnmanaged);
        Assert.Empty(await service.ListAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    private static HttpResponseMessage Json(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                content,
                Encoding.UTF8,
                "application/json"),
        };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHandler(
            Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
