using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.Subscriptions;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AniMeido.Tests;

public sealed partial class SourceSubscriptionServiceTests
{
    private const string CacheRepositoryUrl = "https://github.com/example/EasyBangumi/tree/main/inner_source";
    private const string FirstSourcePath = "inner_source/first.js";

    [Fact]
    public async Task PreviewCache_RestartReusesUnchangedInstalledSourceVersionsWithoutBodies()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, installer, _) = CreateSubscriptionFixture(client);
        await InstallInitialSourcesAsync(service);
        var installed = await installer.ListAsync(default);
        handler.Requests.Clear();

        var restarted = CreateSubscriptionFixture(client).Service;
        var preview = await restarted.PreviewAsync(CacheRepositoryUrl, default);

        Assert.Equal(2, preview.Items.Count);
        Assert.Empty(handler.BodyRequests);
        Assert.All(preview.Items, item =>
        {
            Assert.Equal(SubscriptionChangeKind.Unchanged, item.Change);
            Assert.Null(item.Content);
        });
        Assert.Equal(installed, await installer.ListAsync(default));
        Assert.All(installed, package => Assert.False(package.IsEnabled));
    }

    [Fact]
    public async Task PreviewCache_LearnsLegacyRecordsWithoutApplyingOrReinstallingSources()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, installer, statePath) = CreateSubscriptionFixture(client);
        await InstallInitialSourcesAsync(service);
        await RemoveCacheMarkersAsync(statePath);
        var installed = await installer.ListAsync(default);
        handler.Requests.Clear();

        var learned = await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);
        Assert.Equal(2, handler.BodyRequests.Count);
        Assert.All(learned.Items, item => Assert.Null(item.Content));
        handler.Requests.Clear();
        var cached = await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);

        Assert.Empty(handler.BodyRequests);
        Assert.All(cached.Items, item => Assert.Equal(SubscriptionChangeKind.Unchanged, item.Change));
        Assert.Equal(installed, await installer.ListAsync(default));
    }

    [Fact]
    public async Task PreviewCache_OnlyDownloadsChangedFileAndDoesNotCommitItBeforeApply()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, installer, _) = CreateSubscriptionFixture(client);
        await InstallInitialSourcesAsync(service);
        var installed = await installer.ListAsync(default);
        handler.ChangeFirstFile();
        handler.Requests.Clear();

        var changed = await service.PreviewAsync(CacheRepositoryUrl, default);

        Assert.EndsWith(FirstSourcePath, Assert.Single(handler.BodyRequests).AbsolutePath);
        var update = Assert.Single(changed.Items, item => item.Change == SubscriptionChangeKind.Updated);
        Assert.Equal(FirstSourcePath, update.UpstreamPath);
        Assert.NotNull(update.Content);
        Assert.Null(Assert.Single(changed.Items, item => item.Change == SubscriptionChangeKind.Unchanged).Content);
        Assert.Equal(installed, await installer.ListAsync(default));
        handler.Requests.Clear();
        await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);
        Assert.Single(handler.BodyRequests);

        Assert.Equal(1, (await service.ApplyAsync(changed, default)).AppliedCount);
        handler.Requests.Clear();
        await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);
        Assert.Empty(handler.BodyRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviewCache_MissingOrInvalidLocalPackageRequiresItsBody(bool invalidate)
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, installer, _) = CreateSubscriptionFixture(client);
        var initial = await InstallInitialSourcesAsync(service);
        var first = initial.Items.Single(item => item.UpstreamPath == FirstSourcePath);
        if (invalidate)
        {
            var package = (await installer.ListAsync(default)).Single(item => item.Id == first.SourceId);
            File.Delete(Path.Combine(_testDirectory, "Sources", "Packages", package.Id, package.EntryFile!));
        }
        else
        {
            await installer.UninstallAsync(first.SourceId, default);
        }
        handler.Requests.Clear();

        var preview = await service.PreviewAsync(CacheRepositoryUrl, default);

        Assert.EndsWith(FirstSourcePath, Assert.Single(handler.BodyRequests).AbsolutePath);
        Assert.NotNull(Assert.Single(preview.Items, item => item.UpstreamPath == FirstSourcePath).Content);
    }

    [Fact]
    public async Task PreviewCache_DoesNotReuseMarkerWhoseInstalledRevisionNoLongerMatches()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, _, statePath) = CreateSubscriptionFixture(client);
        await InstallInitialSourcesAsync(service);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        var sources = document["Subscriptions"]![0]!["Sources"]!.AsArray();
        sources.Single(item => (string?)item!["UpstreamPath"] == FirstSourcePath)!["Revision"] = "stale-revision";
        await File.WriteAllTextAsync(statePath, document.ToJsonString());
        handler.Requests.Clear();

        await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);

        Assert.EndsWith(FirstSourcePath, Assert.Single(handler.BodyRequests).AbsolutePath);
    }

    [Fact]
    public async Task PreviewCache_SaveFailureKeepsPreviewUsableAndOldStateUnchanged()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var logger = new PreviewCacheLogger();
        var (service, _, statePath) = CreateSubscriptionFixture(client, logger);
        await InstallInitialSourcesAsync(service);
        await RemoveCacheMarkersAsync(statePath);
        var before = await File.ReadAllTextAsync(statePath);
        using (var held = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var preview = await service.PreviewAsync(CacheRepositoryUrl, default);
            Assert.All(preview.Items, item => Assert.Equal(SubscriptionChangeKind.Unchanged, item.Change));
        }

        Assert.Equal(before, await File.ReadAllTextAsync(statePath));
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Levels));
        handler.Requests.Clear();
        await CreateSubscriptionFixture(client).Service.PreviewAsync(CacheRepositoryUrl, default);
        Assert.Equal(2, handler.BodyRequests.Count);
    }

    [Fact]
    public async Task PreviewCache_CancelledDownloadDoesNotPersistPartialMarkers()
    {
        using var handler = new CacheRepositoryHandler();
        using var client = new HttpClient(handler);
        var (service, _, statePath) = CreateSubscriptionFixture(client);
        await InstallInitialSourcesAsync(service);
        await RemoveCacheMarkersAsync(statePath);
        var before = await File.ReadAllTextAsync(statePath);
        using var cancellation = new CancellationTokenSource();
        handler.BeforeBody = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PreviewAsync(CacheRepositoryUrl, cancellation.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(statePath));
    }

    private (SourceSubscriptionService Service, SourcePackageInstaller Installer, string StatePath)
        CreateSubscriptionFixture(HttpClient client, ILogger<SourceSubscriptionService>? logger = null)
    {
        var installer = new SourcePackageInstaller(Path.Combine(_testDirectory, "Sources"));
        var statePath = Path.Combine(_testDirectory, "subscriptions.json");
        return (new SourceSubscriptionService(new GitHubSourceReader(client), installer, statePath, logger),
            installer, statePath);
    }

    private static async Task<SourceSubscriptionPreview> InstallInitialSourcesAsync(SourceSubscriptionService service)
    {
        var preview = await service.PreviewAsync(CacheRepositoryUrl, default);
        Assert.All(preview.Items, item => Assert.True(item.Change == SubscriptionChangeKind.Added, item.Message));
        Assert.Equal(2, (await service.ApplyAsync(preview, default)).AppliedCount);
        return preview;
    }

    private static async Task RemoveCacheMarkersAsync(string statePath)
    {
        var document = JsonNode.Parse(await File.ReadAllTextAsync(statePath))!;
        foreach (var source in document["Subscriptions"]![0]!["Sources"]!.AsArray())
        {
            source!.AsObject().Remove("UpstreamBlobId");
        }
        await File.WriteAllTextAsync(statePath, document.ToJsonString());
    }

    private sealed class PreviewCacheLogger : ILogger<SourceSubscriptionService>
    {
        internal List<LogLevel> Levels { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(level);
    }

    private sealed record RemoteSource(string BlobId, string Content);

    private sealed class CacheRepositoryHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Dictionary<string, RemoteSource>> _snapshots = new()
        {
            ["commit-1"] = new()
            {
                [FirstSourcePath] = new("first-1", Script("first", 1)),
                ["inner_source/second.js"] = new("second-1", Script("second", 1)),
            },
        };
        private string _current = "commit-1";
        internal ConcurrentQueue<Uri> Requests { get; } = new();
        internal List<Uri> BodyRequests => Requests.Where(uri => uri.Host == "raw.githubusercontent.com").ToList();
        internal Action? BeforeBody { get; set; }

        internal void ChangeFirstFile()
        {
            _snapshots["commit-2"] = new(_snapshots[_current])
            {
                [FirstSourcePath] = new("first-2", Script("first", 2)),
            };
            _current = "commit-2";
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

            BeforeBody?.Invoke();
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var content = _snapshots[segments[2]][string.Join('/', segments.Skip(3))].Content;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8),
            });
        }

        private static string Script(string key, int version)
            => $"// @key {key}\n// @label {key}\n// @versionCode {version}\n// @libVersion 15\n"
                + "function SearchComponent_search() { return new Pair(null, new ArrayList()); }"
                + "function DetailedComponent_getDetailed() { return new Pair(null, new ArrayList()); }"
                + "function PlayComponent_getPlayInfo() { return new PlayerInfo(0, 'https://example.test/video.mp4'); }";
    }
}
