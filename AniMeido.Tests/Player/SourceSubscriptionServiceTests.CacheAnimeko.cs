using AniMeido.Plugin.Player.Sources.Subscriptions;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class SourceSubscriptionServiceTests
{
    private const string AnimekoCacheRepositoryUrl = "https://github.com/example/ani-subs/tree/main";
    private const string FirstAnimekoPath = "subs/web/t0/first.json";

    [Fact]
    public async Task PreviewCache_AnimekoWebReusesInstalledRevisionsAcrossRestartAndSingleFileUpdate()
    {
        using var handler = new AnimekoCacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, installer, _) = CreateSubscriptionFixture(client);

        var initial = await service.PreviewAsync(AnimekoCacheRepositoryUrl, default);
        Assert.Equal(SourceSubscriptionKind.AnimekoWeb, initial.Kind);
        Assert.Equal(2, initial.Items.Count);
        Assert.All(initial.Items, item => Assert.True(item.Change == SubscriptionChangeKind.Added, item.Message));
        Assert.Equal(2, (await service.ApplyAsync(initial, default)).AppliedCount);
        var installed = await installer.ListAsync(default);
        Assert.All(installed, package =>
        {
            Assert.Equal("animeko-web-selector", package.SourceKind);
            Assert.False(package.IsEnabled);
        });
        handler.Requests.Clear();

        var restarted = CreateSubscriptionFixture(client).Service;
        var cached = await restarted.PreviewAsync(AnimekoCacheRepositoryUrl, default);

        Assert.Empty(handler.BodyRequests);
        Assert.Equal(2, cached.Items.Count);
        Assert.All(cached.Items, item =>
        {
            Assert.Equal(SubscriptionChangeKind.Unchanged, item.Change);
            Assert.Null(item.Content);
        });

        handler.ChangeFirstFile();
        handler.Requests.Clear();
        var changed = await restarted.PreviewAsync(AnimekoCacheRepositoryUrl, default);

        Assert.EndsWith(FirstAnimekoPath, Assert.Single(handler.BodyRequests).AbsolutePath);
        var update = Assert.Single(changed.Items, item => item.Change == SubscriptionChangeKind.Updated);
        Assert.Equal(FirstAnimekoPath, update.UpstreamPath);
        Assert.NotNull(update.Content);
        Assert.Null(Assert.Single(changed.Items, item => item.Change == SubscriptionChangeKind.Unchanged).Content);
        Assert.Equal(installed, await installer.ListAsync(default));

        Assert.Equal(1, (await restarted.ApplyAsync(changed, default)).AppliedCount);
        var updatedPackage = (await installer.ListAsync(default)).Single(package => package.Id == update.SourceId);
        Assert.Equal(new Version(0, 0, 2), updatedPackage.Version);
        Assert.False(updatedPackage.IsEnabled);
        handler.Requests.Clear();

        var afterApply = await CreateSubscriptionFixture(client).Service.PreviewAsync(AnimekoCacheRepositoryUrl, default);

        Assert.Empty(handler.BodyRequests);
        Assert.All(afterApply.Items, item =>
        {
            Assert.Equal(SubscriptionChangeKind.Unchanged, item.Change);
            Assert.Null(item.Content);
        });
    }

    private sealed record AnimekoCacheFile(string BlobId, string Content);

    private sealed class AnimekoCacheRepositoryHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Dictionary<string, AnimekoCacheFile>> _snapshots = new()
        {
            ["revision-one"] = new()
            {
                [FirstAnimekoPath] = new("first-json-1", Selector("First 1")),
                ["subs/web/t0/second.json"] = new("second-json-1", Selector("Second 1")),
            },
        };
        private string _current = "revision-one";
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        internal List<Uri> BodyRequests => Requests.Where(uri => uri.Host == "raw.githubusercontent.com").ToList();

        internal void ChangeFirstFile()
        {
            _snapshots["revision-two"] = new(_snapshots[_current])
            {
                [FirstAnimekoPath] = new("first-json-2", Selector("First 2")),
            };
            _current = "revision-two";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Requests.Enqueue(uri);
            if (uri.Host == "api.github.com")
            {
                object response = uri.AbsolutePath.Contains("/commits/", StringComparison.Ordinal)
                    ? new { sha = _current, commit = new { tree = new { sha = "tree-" + _current } } }
                    : new
                    {
                        truncated = false,
                        tree = _snapshots[Path.GetFileName(uri.AbsolutePath)["tree-".Length..]]
                            .Select(item => new { path = item.Key, type = "blob", sha = item.Value.BlobId }),
                    };
                return Task.FromResult(Json(JsonSerializer.Serialize(response)));
            }

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var content = _snapshots[segments[2]][string.Join('/', segments.Skip(3))].Content;
            return Task.FromResult(Json(content));
        }

        private static string Selector(string name)
            => $$"""
                {
                  "factoryId": "web-selector",
                  "version": 2,
                  "arguments": {
                    "name": "{{name}}",
                    "searchConfig": {
                      "searchUrl": "https://example.test/search?q={keyword}",
                      "subjectFormatId": "a",
                      "selectorSubjectFormatA": { "selectLists": "a.result" },
                      "channelFormatId": "no-channel",
                      "selectorChannelFormatNoChannel": { "selectEpisodes": "a.episode" },
                      "onlySupportsPlayers": [],
                      "matchVideo": { "matchVideoUrl": "https://.+\\.m3u8" }
                    }
                  }
                }
                """;
    }
}
