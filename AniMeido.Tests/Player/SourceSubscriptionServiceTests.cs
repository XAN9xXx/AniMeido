using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using System.Net;
using System.Text;

namespace AniMeido.Tests;

public sealed partial class SourceSubscriptionServiceTests : IDisposable
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

    [Fact]
    public async Task Apply_ContinuesPastFailedItemAndPersistsSuccessfulSiblings()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no network")));
        var sources = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sources);
        var state = Path.Combine(_testDirectory, "subscriptions.json");
        var service = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);
        Directory.CreateDirectory(Path.Combine(sources, "Packages"));
        var blocked = Path.Combine(sources, "Packages", "blocked");
        await File.WriteAllTextAsync(blocked, "foreign file");
        var preview = PreviewFor("first", "blocked", "last");

        var result = await service.ApplyAsync(preview, CancellationToken.None);

        Assert.Equal(2, result.AppliedCount);
        Assert.Equal(1, result.FailedCount);
        Assert.False(result.Items[1].Applied);
        Assert.Equal("foreign file", await File.ReadAllTextAsync(blocked));
        var restarted = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);
        Assert.Equal(new[] { "first", "last" },
            Assert.Single(await restarted.ListAsync(CancellationToken.None)).Sources
                .Select(source => source.SourceId).Order().ToArray());

        File.Delete(blocked);
        var retry = await restarted.ApplyAsync(PreviewFor("blocked"), CancellationToken.None);
        Assert.Equal(1, retry.AppliedCount);
        Assert.Equal(3, (await installer.ListAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Remove_PartialFailureKeepsRemainingSourcesManagedForRetry()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no network")));
        var sources = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sources);
        var state = Path.Combine(_testDirectory, "subscriptions.json");
        var service = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);
        var preview = PreviewFor("first", "last");
        await service.ApplyAsync(preview, CancellationToken.None);
        await installer.SetEnabledAsync("first", true, CancellationToken.None);
        await installer.SetEnabledAsync("last", true, CancellationToken.None);
        var blockedMarker = Path.Combine(sources, "Packages", "last", SourcePackageDiscovery.DisabledMarkerName);
        Directory.CreateDirectory(blockedMarker);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RemoveAsync(preview.SubscriptionId, CancellationToken.None));

        var installed = await installer.ListAsync(CancellationToken.None);
        var first = installed.Single(item => item.Id == "first");
        var last = installed.Single(item => item.Id == "last");
        Assert.False(first.IsEnabled);
        Assert.True(first.IsUnmanaged);
        Assert.True(last.IsEnabled);
        Assert.False(last.IsUnmanaged);

        var restarted = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);
        var remaining = Assert.Single(await restarted.ListAsync(CancellationToken.None));
        Assert.Equal("last", Assert.Single(remaining.Sources).SourceId);

        Directory.Delete(blockedMarker);
        await restarted.RemoveAsync(preview.SubscriptionId, CancellationToken.None);

        Assert.Empty(await restarted.ListAsync(CancellationToken.None));
        installed = await installer.ListAsync(CancellationToken.None);
        Assert.Equal(2, installed.Count);
        Assert.All(installed, package =>
        {
            Assert.False(package.IsEnabled);
            Assert.True(package.IsUnmanaged);
        });
    }

    [Fact]
    public async Task MarkOrphaned_DisableFailureDoesNotCommitOrphanMarker()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no network")));
        var sources = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sources);
        var service = new SourceSubscriptionService(new GitHubSourceReader(http), installer,
            Path.Combine(_testDirectory, "subscriptions.json"));
        await service.ApplyAsync(PreviewFor("source"), CancellationToken.None);
        await installer.SetEnabledAsync("source", true, CancellationToken.None);
        var blockedMarker = Path.Combine(sources, "Packages", "source", SourcePackageDiscovery.DisabledMarkerName);
        Directory.CreateDirectory(blockedMarker);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            installer.MarkOrphanedAsync("source", CancellationToken.None));

        var installed = Assert.Single(await installer.ListAsync(CancellationToken.None));
        Assert.True(installed.IsEnabled);
        Assert.False(installed.IsOrphaned);

        Directory.Delete(blockedMarker);
        await installer.MarkOrphanedAsync("source", CancellationToken.None);

        installed = Assert.Single(await installer.ListAsync(CancellationToken.None));
        Assert.False(installed.IsEnabled);
        Assert.True(installed.IsOrphaned);
    }

    [Fact]
    public async Task List_ReconcilesManifestWhenStateMissedTheLastInstalledSource()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("no network")));
        var sources = Path.Combine(_testDirectory, "Sources");
        var installer = new SourcePackageInstaller(sources);
        var state = Path.Combine(_testDirectory, "subscriptions.json");
        var service = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);
        await service.ApplyAsync(PreviewFor("first"), CancellationToken.None);
        var beforeLastInstall = await File.ReadAllTextAsync(state);
        await service.ApplyAsync(PreviewFor("last"), CancellationToken.None);
        // 模拟包目录已提交、订阅状态尚未提交就退出。
        await File.WriteAllTextAsync(state, beforeLastInstall);
        var restarted = new SourceSubscriptionService(new GitHubSourceReader(http), installer, state);

        Assert.Equal(new[] { "first", "last" },
            Assert.Single(await restarted.ListAsync(CancellationToken.None)).Sources
                .Select(source => source.SourceId).Order().ToArray());
    }

    private static SourceSubscriptionPreview PreviewFor(params string[] ids)
        => new("synthetic-subscription", "https://example.test/synthetic", SourceSubscriptionKind.EasyBangumi,
            ids.Select(id => new SubscriptionPreviewItem(
                id + ".js", id, id, "synthetic-revision", 1, SubscriptionChangeKind.Added)
            {
                Content = "// synthetic source, never executed",
            }).ToArray());

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
            => Task.FromResult(request.RequestUri!.Host == "api.github.com"
                && request.RequestUri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                    ? Json("""{"sha":"fixed-commit","commit":{"tree":{"sha":"fixed-tree"}}}""")
                    : _handler(request));
    }
}
