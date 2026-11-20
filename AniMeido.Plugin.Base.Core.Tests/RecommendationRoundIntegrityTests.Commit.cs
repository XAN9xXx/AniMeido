using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Reflection;

namespace AniMeido.Tests;

public sealed partial class RecommendationRoundIntegrityTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task RestartedRecommendationCacheHitQueuesOnePersonalRemapAtTheAlreadyCheckedVersion()
    {
        var archive = new ArchiveService(_db.Factory);
        await archive.UpsertArchiveAsync(101, "旧作品", 8, "保留摘要");
        await ExecuteAsync("INSERT INTO config(Key,Value) VALUES('public-id-check-version','20261112.1')");
        var previousCache = new CacheService(_db.Factory);
        await previousCache.SetCacheAsync("metadata:detail:101", """
            {"dataVersion":"20261112.1","data":{"id":201,"names":[{"kind":"main","lang":"zh","text":"合并后作品"}]}}
            """, TimeSpan.FromDays(7));
        await previousCache.SetCacheAsync(MetadataDataSource.VersionCacheKey, "20261112.1", TimeSpan.FromDays(7));

        var statusCalls = 0;
        var detailCalls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Head) return Json(new { });
            if (request.RequestUri!.PathAndQuery == "/v1/works?ids=101")
            {
                Interlocked.Increment(ref statusCalls);
                return Json(new { items = new[] { new { id = 101, status = "merged", mergedInto = 201 } } });
            }
            Interlocked.Increment(ref detailCalls);
            throw new InvalidOperationException("本场景应读取持久详情缓存。");
        })) { BaseAddress = new Uri("https://api.animeido.com:38443") };
        var restartedCache = new CacheService(_db.Factory);
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, restartedCache);
        var remap = new PersonalIdRemapService(_db.Factory, NullLogger<PersonalIdRemapService>.Instance, restartedCache);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remap.Changed += () => changed.TrySetResult();
        await using var checker = new PublicIdCheckService(_db.Factory, client, remote, remap, NullLogger<PublicIdCheckService>.Instance);
        await using var provider = new RecommendationCandidateProvider(new PersonalAnimeDataSource(remote, _db.Factory), NullLogger<RecommendationCandidateProvider>.Instance);
        var gate = (SemaphoreSlim)typeof(PublicIdCheckService).GetField("_checkGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(checker)!;
        await gate.WaitAsync();
        try
        {
            checker.Start();
            for (var i = 0; i < 3; i++)
            {
                var seeds = await provider.ResolveTitlesAsync([new(101, "", 1)], default);
                Assert.Equal("合并后作品", Assert.Single(seeds).Title);
            }
            var queued = (System.Collections.Concurrent.ConcurrentDictionary<int, byte>)typeof(PublicIdCheckService)
                .GetField("_queuedDetails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(checker)!;
            Assert.Equal(101, Assert.Single(queued).Key);
            Assert.Equal(0, detailCalls);
            Assert.Equal(0, statusCalls);
            Assert.NotNull(await archive.GetArchiveAsync(101));
        }
        finally { gate.Release(); }

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(await archive.GetArchiveAsync(101));
        var saved = await archive.GetArchiveAsync(201);
        Assert.NotNull(saved);
        Assert.Equal(8, saved.PersonalRating);
        Assert.Equal("保留摘要", saved.SummaryNote);
        Assert.Equal(1, statusCalls);
        Assert.Equal(0, detailCalls);
    }

    [Fact]
    public async Task SuccessfulFeaturesDoNotPublishAnUnconfirmedIdentity()
    {
        var detailCalls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return Json(new { });
            if (uri.AbsolutePath == "/v1/vocab") return Json(new { items = new[] { new { category = "tag", code = "a", nameZh = "A" } } });
            if (uri.AbsolutePath == "/v1/brief") return Json(new { items = new[] { new { id = 101, firstDate = "2026-01-01", ratings = new[] { new { platform = "bangumi", score = 8, votes = 1000 } } } } });
            if (uri.AbsolutePath == "/v1/works") return Json(new { items = new[] { new { id = 101, firstDate = "2026-01-01" } } });
            if (uri.AbsolutePath == "/v1/works/101")
            {
                if (++detailCalls == 1) throw new HttpRequestException("第一次详情读取失败");
                var response = Json(new { code = "merged", mergedInto = 201 }); response.StatusCode = HttpStatusCode.NotFound; return response;
            }
            return Json(new { data = new { id = 201, firstDate = "2026-01-01", tags = new[] { "a" } } });
        })) { BaseAddress = new Uri("https://api.animeido.com:38443") };
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(_db.Factory));
        await using var provider = new RecommendationCandidateProvider(new PersonalAnimeDataSource(remote, _db.Factory), NullLogger<RecommendationCandidateProvider>.Instance);
        var round = provider.CreateRound([Tag], new HashSet<int> { 201 }, new DateOnly(2026, 11, 12));
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.True(round.HasMore);
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.False(round.HasMore);
        Assert.Equal(2, detailCalls);
    }

    [Fact]
    public async Task RepeatedDetailFailureExitsWithinItsExistingBudget()
    {
        var source = new Source { Ids = [101], DetailFailures = 2 };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Tag], new HashSet<int>());
        for (var i = 0; i < 3; i++) Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.False(round.HasMore); Assert.Equal(2, source.Calls[(101, "detail")]);
        Assert.Equal(1, source.Calls[(101, "tags")]);
    }

    [Fact]
    public async Task MissingDetailRemovesTheCandidateInsteadOfUsingItsSourceIdentity()
    {
        var source = new Source { Ids = [101], MissingDetails = true };
        await using var provider = Provider(source);
        var round = provider.CreateRound([Tag], new HashSet<int>());
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.False(round.HasMore); Assert.Equal(1, source.Calls[(101, "detail")]);
    }

    [Fact]
    public async Task FailedAliasExclusionAlsoRemovesSuccessfulAliasFromTheSameWindow()
    {
        var source = new Source { Ids = [101, 102], Canonical = _ => 201, FailTag = (id, attempt) => id == 101 || attempt == 1 };
        await using var provider = Provider(source);
        RecommendationFeatureProfile negative = new(new(RecommendationFeatureKind.Tag, "B", "B"), -2, null, []);
        var round = provider.CreateRound([Tag, negative], new HashSet<int>());
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.Empty(await provider.GetNextWindowAsync(round, 30, default));
        Assert.Contains(201, round.ExcludedIds); Assert.False(round.HasMore);
        Assert.Equal(2, source.Calls[(101, "tags")]); Assert.Equal(2, source.Calls[(102, "tags")]);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CancellationDuringAcceptedCacheWriteCommitsOneRecoverableBatch()
    {
        var source = new Source { Ids = Enumerable.Range(1, 21).ToArray() };
        await using var provider = Provider(source);
        var cache = new CacheService(_db.Factory);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider, cache);
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        var first = (await service.RefreshAsync())!.Snapshot;
        Assert.Equal(20, first.Items.Count); Assert.True(first.HasMore);
        var gate = (SemaphoreSlim)typeof(CacheService).GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        using var cancellation = new CancellationTokenSource();
        await gate.WaitAsync();
        Task<RecommendationPageBatch>? loading = null;
        try
        {
            loading = service.LoadMoreAsync(first, new HashSet<int>(), cancellation.Token);
            await WaitForQueuedAsync(gate, loading);
            cancellation.Cancel();
        }
        finally { gate.Release(); }
        var next = await loading!;
        Assert.Single(next.Items); Assert.Equal(21, next.Snapshot.Items.Count); Assert.False(next.Snapshot.HasMore);
        using var store = new RecommendationSnapshotStore(cache);
        Assert.Equal(21, (await store.ReadAsync(true, default))!.Items.Count);
        var fromCache = (await service.GetCachedSnapshotAsync(true))!;
        var fromMemory = await service.FilterRatedItemsAsync(first);
        Assert.Equal(21, fromCache.Items.Count); Assert.False(fromCache.HasMore);
        Assert.Equal(21, fromMemory.Items.Count); Assert.False(fromMemory.HasMore);
        Assert.Empty((await service.LoadMoreAsync(fromMemory, new HashSet<int>())).Items);
    }

    [Fact]
    public async Task RemovingRatingDoesNotRestoreAFilteredWorkInTheSameRound()
    {
        await using var provider = Provider(new Source { Ids = [1, 2] });
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        var first = (await service.RefreshAsync())!.Snapshot;
        var archive = new ArchiveService(_db.Factory);
        await archive.UpsertArchiveAsync(1, "作品1", 8, "");
        var filtered = await service.FilterRatedItemsAsync(first);
        Assert.Equal(2, Assert.Single(filtered.Items).Anime.ID);
        await archive.UpsertArchiveAsync(1, "作品1", null, "");
        Assert.Equal(2, Assert.Single((await service.FilterRatedItemsAsync(filtered)).Items).Anime.ID);
        Assert.Equal(2, Assert.Single((await service.GetCachedSnapshotAsync(true))!.Items).Anime.ID);
        Assert.Equal(2, Assert.Single((await service.LoadMoreAsync(first, new HashSet<int>())).Snapshot.Items).Anime.ID);
        Assert.Contains((await service.RefreshAsync())!.Snapshot.Items, item => item.Anime.ID == 1);
    }

    private static async Task WaitForQueuedAsync(SemaphoreSlim gate, Task operation)
    {
        var queued = typeof(SemaphoreSlim).GetField("m_asyncHead", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (queued.GetValue(gate) is null && !operation.IsCompleted && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.NotNull(queued.GetValue(gate));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task ConcurrentFilterWaitsForContinuationAndKeepsItsNewItems()
    {
        await using var provider = Provider(new Source { Ids = Enumerable.Range(1, 21).ToArray() });
        var cache = new CacheService(_db.Factory);
        await using var service = RecommendationTestServices.CreateService(_db.Factory, provider, cache);
        await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
        var first = (await service.RefreshAsync())!.Snapshot;
        var mutation = (SemaphoreSlim)typeof(CacheService).GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        var refresh = (SemaphoreSlim)typeof(RecommendationService).GetField("_refreshGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        await mutation.WaitAsync();
        Task<RecommendationPageBatch>? continuation = null;
        Task<RecommendationSnapshot>? filtering = null;
        try
        {
            continuation = service.LoadMoreAsync(first, new HashSet<int>());
            await WaitForQueuedAsync(mutation, continuation);
            filtering = service.FilterRatedItemsAsync(first);
            await WaitForQueuedAsync(refresh, filtering);
        }
        finally
        {
            mutation.Release();
            if (continuation is not null) await continuation;
            if (filtering is not null) await filtering;
        }
        Assert.Equal(21, (await filtering!).Items.Count);
        Assert.False((await filtering).HasMore);
        Assert.Equal(21, (await service.GetCachedSnapshotAsync(true))!.Items.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task KnownMergeExcludesCanonicalCandidateWhilePersonalRemapIsQueued(bool rated)
    {
        if (rated) await new ArchiveService(_db.Factory).UpsertArchiveAsync(101, "旧作品", 8, "");
        else await new TrackingService(_db.Factory).SetStatusAsync(101, AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked);
        using var client = new HttpClient(new Handler(request =>
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Head) return Json(new { });
            if (uri.AbsolutePath == "/v1/vocab") return Json(new { items = new[] { new { category = "tag", code = "a", nameZh = "A" } } });
            if (uri.AbsolutePath == "/v1/brief") return Json(new { items = new[] { new { id = 201, firstDate = "2026-01-01", ratings = new[] { new { platform = "bangumi", score = 8, votes = 1000 } } } } });
            if (uri.AbsolutePath == "/v1/works") return Json(new { items = new[] { new { id = 201, firstDate = "2026-01-01" } }, total = 1 });
            if (uri.AbsolutePath == "/v1/works/101")
            {
                var response = Json(new { code = "merged", mergedInto = 201 }); response.StatusCode = HttpStatusCode.NotFound; return response;
            }
            return Json(new { data = new { id = 201, firstDate = "2026-01-01", tags = new[] { "a" } } });
        })) { BaseAddress = new Uri("https://api.animeido.com:38443") };
        var remote = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, new CacheService(_db.Factory));
        var personal = new PersonalAnimeDataSource(remote, _db.Factory);
        var remap = new PersonalIdRemapService(_db.Factory, NullLogger<PersonalIdRemapService>.Instance);
        await using var checker = new PublicIdCheckService(_db.Factory, client, remote, remap, NullLogger<PublicIdCheckService>.Instance);
        var gate = (SemaphoreSlim)typeof(PublicIdCheckService).GetField("_checkGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(checker)!;
        await gate.WaitAsync();
        try
        {
            checker.Start();
            Assert.Equal(101, (await personal.GetAnimeDetailAsync(101, default))!.ID);
            await using var provider = new RecommendationCandidateProvider(personal, NullLogger<RecommendationCandidateProvider>.Instance);
            await using var service = RecommendationTestServices.CreateService(_db.Factory, provider);
            await service.SetFeaturePreferenceAsync(Tag.Feature, RecommendationAdjustment.Like);
            Assert.DoesNotContain((await service.RefreshAsync())!.Snapshot.Items, item => item.Anime.ID == 201);
            if (rated) Assert.NotNull(await new ArchiveService(_db.Factory).GetArchiveAsync(101));
            else Assert.Equal(AniMeido.Contracts.Models.AnimeTrackingStatus.Blocked, await new TrackingService(_db.Factory).GetStatusAsync(101));
        }
        finally { gate.Release(); }
    }
}
