using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Reflection;

namespace AniMeido.Tests;

public sealed partial class RecommendationRoundIntegrityTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _db = new();
    public Task InitializeAsync() => _db.InitializeAsync();
    public Task DisposeAsync() => _db.DisposeAsync();
    private static RecommendationFeatureProfile Tag => new(new(RecommendationFeatureKind.Tag, "A", "A"), 2, null, []);
    private static RecommendationCandidateProvider Provider(Source source) => new(source, NullLogger<RecommendationCandidateProvider>.Instance);

    [Fact]
    public async Task CanonicalIdentityIsDeduplicatedConsumedAndExcluded()
    {
        var source = new Source { Ids = [101, 102], Canonical = _ => 201 };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Tag], new HashSet<int>());
        var first = await provider.GetNextWindowAsync(round, 30, default);
        Assert.Equal(201, Assert.Single(first).Anime.ID);
        round.Consume([201]);
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.False(round.HasMore);
        var excluded = provider.CreateRound([Tag], new HashSet<int> { 201 });
        Assert.Empty(await provider.GetNextWindowAsync(excluded, 30, default));
        Assert.False(excluded.HasMore);
        var failed = provider.CreateRound([Tag, new(new(RecommendationFeatureKind.Tag, "B", "B"), -2, null, [])], new HashSet<int>());
        await provider.GetNextWindowAsync(failed, 30, default);
        failed.Exclude([101]);
        Assert.Contains(201, failed.ExcludedIds);
        Assert.Empty(failed.Pending);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshNeverReturnsACanonicalHiddenOrBlockedWork(bool blocked)
    {
        await using var provider = Provider(new Source { Ids = [101], Canonical = _ => 201 });
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        if (blocked) await new TrackingService(_db.Factory).SetStatusAsync(201, AnimeTrackingStatus.Blocked);
        else await service.HideAnimeAsync(201, "隐藏作品");
        var result = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.DoesNotContain(result.Snapshot.Items, item => item.Anime.ID == 201);
        Assert.False(result.Snapshot.HasMore);
    }

    [Fact]
    public async Task PopularFallbackPreservesTheTwoFailureLimit()
    {
        var source = new Source { Ids = [1], Popular = true, TagFailures = 2 };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        await service.SetFeaturePreferenceAsync(new(RecommendationFeatureKind.Tag, "B", "B"), RecommendationAdjustment.Reduce);
        var result = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.Empty(result.Snapshot.Items);
        Assert.Equal(2, source.Calls[(1, "tags")]);
    }

    [Theory]
    [InlineData(RecommendationFeatureKind.VoiceActor)]
    [InlineData(RecommendationFeatureKind.Studio)]
    public async Task PersonSourceDoesNotOverrideSuccessfullyEmptyRelations(RecommendationFeatureKind kind)
    {
        await using var provider = Provider(new Source { Ids = [1] });
        RecommendationFeatureProfile profile = new(new(kind, "42", "某人"), 5, null, []);
        var round = provider.CreateRound([profile], new HashSet<int>());
        var window = await provider.GetNextWindowAsync(round, 30, default);
        Assert.DoesNotContain(Assert.Single(window).Features, feature => feature.Kind == kind);
        Assert.Empty(RecommendationScorer.Rank([profile], window, DateOnly.FromDateTime(DateTime.Today)));
    }

    [Fact]
    public async Task FailureAfterRankingKeepsTheUnpublishedBatchAndEnrichment()
    {
        var source = new Source { Ids = [1] };
        source.AfterActors = async () =>
        {
            await ExecuteAsync("ALTER TABLE anime_archives RENAME TO paused_archives;");
            source.AfterActors = null;
        };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, true, [], [Tag], true);
        try { await Assert.ThrowsAsync<SqliteException>(() => service.LoadMoreAsync(snapshot, new HashSet<int>())); }
        finally { await ExecuteAsync("ALTER TABLE paused_archives RENAME TO anime_archives;"); }
        var retry = await service.LoadMoreAsync(snapshot, new HashSet<int>());
        Assert.Equal(1, Assert.Single(retry.Items).Anime.ID);
        Assert.False(retry.Snapshot.HasMore);
        Assert.Equal(1, source.Calls[(1, "detail")]);
        Assert.Equal(1, source.Calls[(1, "actors")]);
    }

    [Fact]
    public async Task SuccessfulBatchCanBeRecoveredFromAnOlderInMemorySnapshot()
    {
        await using var provider = Provider(new Source { Ids = [1, 2] });
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, true, [], [Tag], true);
        var page = await service.LoadMoreAsync(snapshot, new HashSet<int>());
        Assert.Equal(2, page.Items.Count);
        var restored = await service.FilterRatedItemsAsync(snapshot);
        Assert.Equal(page.Snapshot.Items.Select(item => item.Anime.ID), restored.Items.Select(item => item.Anime.ID));
        Assert.False(restored.HasMore);
    }

    private async Task ExecuteAsync(string sql)
    {
        using var connection = await _db.Factory.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CancellationAtSnapshotCommitKeepsCandidatesForRetry()
    {
        var source = new Source { Ids = [1] };
        await using var provider = Provider(source);
        var cache = new CacheService(_db.Factory);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider, cache);
        var snapshot = new RecommendationSnapshot(RecommendationSnapshot.CurrentSchemaVersion, DateTimeOffset.UtcNow, true, [], [Tag], true);
        await cache.SetCacheAsync("recommendations:metadata:snapshot:v1", JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }), TimeSpan.FromDays(1));
        var store = (RecommendationSnapshotStore)typeof(RecommendationService).GetField("_snapshots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var gate = (SemaphoreSlim)typeof(RecommendationSnapshotStore).GetField("_writeGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var cancellation = new CancellationTokenSource();
        // 先取走写入资格，只有进入 TryWriteAsync 的等待者才能触发下面的排队检查。
        await gate.WaitAsync();
        Task<RecommendationPageBatch>? loading = null;
        try
        {
            loading = service.LoadMoreAsync(snapshot, new HashSet<int>(), cancellation.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            var queued = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
            while (queued.GetValue(gate) is null && !loading.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.NotNull(queued.GetValue(gate));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        }
        finally { gate.Release(); if (loading is not null) await ((Task)loading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext); }
        Assert.Single((await service.LoadMoreAsync(snapshot, new HashSet<int>())).Items);
        Assert.Equal(1, source.Calls[(1, "detail")]);
    }

    [Fact]
    public async Task GenerationAcrossMidnightKeepsTheRoundStartDateAfterRestart()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 12, 23, 59, 0, TimeSpan.Zero));
        var started = clock.Now;
        var source = new Source { Ids = Enumerable.Range(1, 25).ToArray(), AfterActors = () => { clock.Now = started.AddMinutes(2); return Task.CompletedTask; } };
        await using var provider = Provider(source);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        service.Clock = clock;
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        var first = Assert.IsType<RecommendationGeneration>(await service.RefreshAsync());
        Assert.Equal(started, first.Snapshot.GeneratedAt);
        Assert.Contains(source.Ranges, range => range.From == "2023-11-12");
        var restartedSource = new Source { Ids = Enumerable.Range(1, 25).ToArray() };
        await using var restartedProvider = Provider(restartedSource);
        await using var restarted = RecommendationTestServices.CreateService(_db.Factory, restartedProvider);
        restarted.Clock = clock;
        await restarted.LoadMoreAsync(first.Snapshot, new HashSet<int>());
        Assert.Contains(restartedSource.Ranges, range => range.From == "2023-11-12");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionTagPagingAdvancesPastFilteredRawIds(bool partial)
    {
        var requested = new ConcurrentBag<int>();
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return Json(new { });
            if (uri.AbsolutePath == "/v1/vocab") return Json(new { items = new[] { new { category = "tag", code = "a", nameZh = "A" } } });
            if (uri.AbsolutePath == "/v1/brief") return Json(new { items = Enumerable.Range(1, 21).Select(id => new { id, firstDate = "2026-01-01", ratings = new[] { new { platform = "bangumi", score = 8, votes = 1000 } } }) });
            if (uri.AbsolutePath == "/v1/works")
            {
                var ids = Uri.UnescapeDataString(uri.Query[5..]).Split(',').Select(int.Parse).ToArray();
                foreach (var id in ids) requested.Add(id);
                return Json(new { items = ids.Where(id => partial ? id != 1 : id == 21).Select(id => new { id, firstDate = "2026-01-01" }) });
            }
            var workId = int.Parse(uri.Segments[^1]);
            return Json(new { data = new { id = workId, firstDate = "2026-01-01", tags = new[] { "a" } } });
        })) { BaseAddress = new Uri("https://api.animeido.com:38443") };
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(_db.Factory));
        await using var provider = new RecommendationCandidateProvider(new PersonalAnimeDataSource(remote, _db.Factory), NullLogger<RecommendationCandidateProvider>.Instance);
        var round = provider.CreateRound([Tag], new HashSet<int>(), new DateOnly(2026, 11, 12));
        var window = await provider.GetNextWindowAsync(round, 30, default);
        Assert.Contains(window, candidate => candidate.Anime.ID == 21);
        Assert.Equal(partial ? 20 : 1, window.Count);
        Assert.Equal(21, requested.Count);
        Assert.Equal(21, requested.Distinct().Count());
    }

    [Fact]
    public async Task ProductionAdapterUsesCanonicalIdentityOnlyForRecommendation()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return Json(new { });
            if (uri.AbsolutePath == "/v1/vocab") return Json(new { items = new[] { new { category = "tag", code = "a", nameZh = "A" } } });
            if (uri.AbsolutePath == "/v1/brief") return Json(new { items = new[] { new { id = 101, firstDate = "2026-01-01", ratings = new[] { new { platform = "bangumi", score = 8, votes = 1000 } } } } });
            if (uri.AbsolutePath == "/v1/works") return Json(new { items = new[] { new { id = 101, firstDate = "2026-01-01" } } });
            if (uri.AbsolutePath == "/v1/works/101")
            {
                var response = Json(new { code = "merged", mergedInto = 201 }); response.StatusCode = HttpStatusCode.NotFound; return response;
            }
            return Json(new { data = new { id = 201, firstDate = "2026-01-01", tags = new[] { "a" } } });
        })) { BaseAddress = new Uri("https://api.animeido.com:38443") };
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(_db.Factory));
        var personal = new PersonalAnimeDataSource(remote, _db.Factory);
        Assert.Equal(101, (await personal.GetAnimeDetailAsync(101, default))!.ID);
        await using var provider = new RecommendationCandidateProvider(personal, NullLogger<RecommendationCandidateProvider>.Instance);
        var round = provider.CreateRound([Tag], new HashSet<int> { 201 }, new DateOnly(2026, 11, 12));
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.False(round.HasMore);
    }

    private static HttpResponseMessage Json(object value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
        response.Headers.Add("X-Data-Version", "20261112.1"); return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Source : IAnimeDataSource
    {
        public int[] Ids { get; init; } = [1];
        public Func<int, int> Canonical { get; init; } = id => id;
        public bool Popular { get; init; }
        public int TagFailures { get; init; }
        public int DetailFailures { get; init; }
        public bool MissingDetails { get; init; }
        public Func<int, int, bool>? FailTag { get; init; }
        public Func<Task>? AfterActors { get; set; }
        public ConcurrentDictionary<(int, string), int> Calls { get; } = new();
        public ConcurrentBag<(string? From, string? To)> Ranges { get; } = [];
        private int Count(int id, string kind) => Calls.AddOrUpdate((id, kind), 1, (_, n) => n + 1);
        internal static Anime Anime(int id) => new(id, $"作品{id}", null, [], new(2026, 1, 1), null, "", 2026, 1, Score: 8);
        public Task<Anime?> GetAnimeDetailAsync(int id, CancellationToken ct)
        { if (Count(id, "detail") <= DetailFailures) throw new HttpRequestException("合成详情失败"); return Task.FromResult<Anime?>(MissingDetails ? null : Anime(Canonical(id))); }
        public Task<List<Tag>> GetTagsAsync(int id, CancellationToken ct)
        { var attempt = Count(id, "tags"); if (attempt <= TagFailures || FailTag?.Invoke(id, attempt) == true) throw new HttpRequestException("合成失败"); return Task.FromResult(new List<Tag> { new("A") }); }
        public Task<List<Studio>> GetStudioAsync(int id, CancellationToken ct) => Task.FromResult(new List<Studio>());
        public async Task<List<VoiceActor>> GetCVsAsync(int id, CancellationToken ct) { Count(id, "actors"); if (AfterActors is { } callback) await callback(); return []; }
        public Task<List<PersonWork>> GetPersonWorksAsync(int id, CancellationToken ct) => Task.FromResult(Ids.Select(id => new PersonWork(id, $"作品{id}", "导演", null, 8, new(2026, 1, 1))).ToList());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
        { Ranges.Add((airDateFrom, airDateTo)); return Task.FromResult((airDateFrom is null ? [] : Ids.Skip(offset).Take(20).Select(Anime).ToList(), airDateFrom is null ? 0 : Ids.Length)); }
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Task.FromResult(Popular ? Ids.Select(Anime).ToList() : new List<Anime>());
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int id, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
