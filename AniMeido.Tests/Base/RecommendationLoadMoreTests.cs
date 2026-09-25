using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace AniMeido.Tests;

public class RecommendationLoadMoreTests : DbTestBase
{
    private const string SnapshotKey = "recommendations:snapshot:v1";

    [Fact]
    public async Task LoadMore_PersistsShownItemsAndExcludesThemAfterRestart()
    {
        await RunProductionMigrationAsync();
        var source = new PagedSource();
        var cache = new CacheService(DbFactory);
        using var candidates = CreateCandidates(source);
        using var service = CreateService(candidates, cache);
        var first = InitialSnapshot();
        await cache.SetCacheAsync(SnapshotKey,
            JsonSerializer.Serialize(first, JsonOptions), TimeSpan.FromDays(90));

        var next = await service.LoadMoreAsync(first, new HashSet<int>());

        Assert.Equal(20, next.Items.Count);
        Assert.Equal(21, next.Snapshot.Items.Count);
        Assert.Equal(first.GeneratedAt, next.Snapshot.GeneratedAt);
        using var resumedCandidates = CreateCandidates(new PagedSource());
        using var resumed = CreateService(resumedCandidates, new CacheService(DbFactory));
        var restored = await resumed.GetCachedSnapshotAsync(allowExpired: false);
        Assert.NotNull(restored);
        Assert.Equal(next.Snapshot.Items.Select(item => item.Anime.ID),
            restored.Items.Select(item => item.Anime.ID));

        var later = await resumed.LoadMoreAsync(restored, new HashSet<int>());
        Assert.DoesNotContain(later.Items,
            item => restored.Items.Any(shown => shown.Anime.ID == item.Anime.ID));
    }

    [Fact]
    public async Task RefreshDuringLoadMore_ReleasesLoadingState()
    {
        await RunProductionMigrationAsync();
        var source = new PagedSource(blockDetails: true);
        using var candidates = CreateCandidates(source);
        using var service = CreateService(candidates, new CacheService(DbFactory));
        var vm = new RecommendationViewModel(service, new RecommendationBrowseState
        {
            Snapshot = InitialSnapshot(),
        });
        await vm.LoadAsync();

        var loading = vm.LoadMoreAsync();
        await source.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var refreshing = vm.RefreshAsync();
        Assert.False(vm.IsLoadingMore);
        source.ReleaseDetails.TrySetResult();
        await Task.WhenAll(loading, refreshing);

        Assert.False(vm.IsLoadingMore);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task InvalidatedRound_LoadMoreDoesNotRestoreRemovedCache()
    {
        await RunProductionMigrationAsync();
        var cache = new CacheService(DbFactory);
        using var candidates = CreateCandidates(new PagedSource());
        using var service = CreateService(candidates, cache);
        var snapshot = InitialSnapshot();
        await cache.SetCacheAsync(SnapshotKey,
            JsonSerializer.Serialize(snapshot, JsonOptions), TimeSpan.FromDays(90));
        await service.SetFeaturePreferenceAsync(snapshot.RoundProfile![0].Feature,
            RecommendationAdjustment.Like);

        var page = await service.LoadMoreAsync(snapshot, new HashSet<int>());

        Assert.NotEmpty(page.Items);
        Assert.Null(await service.GetCachedSnapshotAsync(allowExpired: true));
    }

    [Fact]
    public async Task HideDuringLoadMore_DoesNotWaitForRemoteRequestOrRestoreCache()
    {
        await RunProductionMigrationAsync();
        var source = new PagedSource(blockDetails: true);
        var cache = new CacheService(DbFactory);
        using var candidates = CreateCandidates(source);
        using var service = CreateService(candidates, cache);
        var snapshot = InitialSnapshot();
        await cache.SetCacheAsync(SnapshotKey,
            JsonSerializer.Serialize(snapshot, JsonOptions), TimeSpan.FromDays(90));
        var loading = service.LoadMoreAsync(snapshot, new HashSet<int>());
        await source.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await service.HideAnimeAsync(2, "作品2").WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            source.ReleaseDetails.TrySetResult();
        }

        var result = await loading;
        Assert.NotEmpty(result.Items);
        Assert.DoesNotContain(result.Items, item => item.Anime.ID == 2);
        Assert.Null(await service.GetCachedSnapshotAsync(allowExpired: true));

        var later = await service.LoadMoreAsync(result.Snapshot, new HashSet<int>());
        Assert.NotEmpty(later.Items);
        Assert.DoesNotContain(later.Items, item => result.Snapshot.Items.Any(
            shown => shown.Anime.ID == item.Anime.ID));
    }

    [Fact]
    public async Task HideDuringAutomaticRefresh_DoesNotShowARefreshError()
    {
        await RunProductionMigrationAsync();
        await new SavedTagService(DbFactory).SaveTagAsync("科幻");
        var source = new PagedSource(blockDetails: true);
        using var candidates = CreateCandidates(source);
        using var service = CreateService(candidates, new CacheService(DbFactory));
        var browse = new RecommendationBrowseState
        {
            Snapshot = InitialSnapshot() with
            {
                GeneratedAt = DateTimeOffset.UtcNow.AddDays(-2),
            },
        };
        var vm = new RecommendationViewModel(service, browse);
        var loading = vm.LoadAsync();
        await source.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await vm.HideAsync(browse.Snapshot.Items[0]).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            source.ReleaseDetails.TrySetResult();
        }

        await loading;
        Assert.False(vm.HasError);
        Assert.Empty(vm.Items);
        Assert.Equal("已从推荐中隐藏，可在“已隐藏”中恢复。", vm.Message);
        Assert.Contains("本次刷新未应用", vm.RefreshNotice);
    }

    [Fact]
    public async Task FollowDuringManualRefresh_ReportsUnappliedResultWithoutReplacingItems()
    {
        await RunProductionMigrationAsync();
        await new SavedTagService(DbFactory).SaveTagAsync("科幻");
        var source = new PagedSource(blockDetails: true);
        using var candidates = CreateCandidates(source);
        using var service = CreateService(candidates, new CacheService(DbFactory));
        var snapshot = InitialSnapshot();
        var vm = new RecommendationViewModel(service, new RecommendationBrowseState
        {
            Snapshot = snapshot,
        });
        await vm.LoadAsync();
        var refreshing = vm.RefreshAsync(preferNewBatch: true);
        await source.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            await vm.ToggleFollowingAsync(snapshot.Items[0], CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            source.ReleaseDetails.TrySetResult();
        }

        Assert.False(await refreshing);
        Assert.False(vm.HasError);
        Assert.Equal(snapshot.Items.Select(item => item.Anime.ID),
            vm.Items.Select(item => item.Anime.ID));
        Assert.Contains("已关注", vm.Message);
        Assert.Contains("本次刷新未应用", vm.RefreshNotice);

        await vm.SetPreferenceAsync(snapshot.RoundProfile![0].Feature,
            RecommendationAdjustment.Like);
        Assert.Equal("偏好已更新，下轮推荐生效。", vm.Message);
        Assert.Contains("本次刷新未应用", vm.RefreshNotice);
    }

    private static RecommendationSnapshot InitialSnapshot()
    {
        var feature = new RecommendationFeature(
            RecommendationFeatureKind.Tag, "科幻", "科幻");
        var profile = new RecommendationFeatureProfile(feature, 2.5, null, [],
            IsSavedTag: true);
        var anime = PagedSource.Anime(1);
        return new RecommendationSnapshot(
            RecommendationSnapshot.CurrentSchemaVersion,
            DateTimeOffset.UtcNow,
            true,
            [new RecommendationItem(anime, 2.5, [], true, true)],
            [profile],
            HasMore: true);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static RecommendationCandidateProvider CreateCandidates(PagedSource source)
        => new(source, NullLogger<RecommendationCandidateProvider>.Instance);

    private RecommendationService CreateService(
        RecommendationCandidateProvider candidates, CacheService cache) => new(
        DbFactory, new TrackingService(DbFactory), new SavedTagService(DbFactory),
        new ArchiveService(DbFactory), new BrowseHistoryService(DbFactory),
        new ActionCenterService(DbFactory), cache, candidates);

    private sealed class PagedSource(bool blockDetails = false) : IAnimeDataSource
    {
        public TaskCompletionSource DetailStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDetails { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public static Anime Anime(int id) => new(id, $"作品{id}", null, [],
            new DateOnly(2026, 1, 1), null, string.Empty, 2026, 1, Score: 8);

        public async Task<Anime?> GetAnimeDetailAsync(int id, CancellationToken ct)
        {
            DetailStarted.TrySetResult();
            if (blockDetails) await ReleaseDetails.Task.WaitAsync(ct);
            return null;
        }

        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag,
            int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null)
        {
            if (airDateFrom is null)
                return Task.FromResult((new List<Anime>(), 0));
            var items = Enumerable.Range(offset + 1, Math.Min(20, 45 - offset))
                .Select(Anime).ToList();
            return Task.FromResult((items, 45));
        }

        public Task<List<Tag>> GetTagsAsync(int id, CancellationToken ct)
            => Task.FromResult(new List<Tag>());
        public Task<List<Studio>> GetStudioAsync(int id, CancellationToken ct)
            => Task.FromResult(new List<Studio>());
        public Task<List<VoiceActor>> GetCVsAsync(int id, CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season,
            CancellationToken ct) => Task.FromResult(new List<Anime>());
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int id,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int id,
            CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
