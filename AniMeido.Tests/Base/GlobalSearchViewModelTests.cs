using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class GlobalSearchViewModelTests : DbTestBase
{
    [Fact]
    public async Task NewSearchResetsPaginationAndIgnoresLateFailureFromOldPage()
    {
        await RunProductionMigrationAsync();
        var oldPage = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SearchSource
        {
            Search = (query, offset, _) => query == "old"
                ? offset == 0 ? Task.FromResult((new List<Anime> { Item(1) }, 2)) : oldPage.Task
                : Task.FromResult((new List<Anime> { Item(offset == 0 ? 2 : 3) }, 2)),
        };
        var vm = NewViewModel(source, new TrackingService(DbFactory));
        await vm.SearchAsync("old");
        var loading = vm.LoadMoreAsync();
        Assert.True(vm.IsLoadingMore);
        try
        {
            await vm.SearchAsync("new");
            Assert.False(vm.IsLoadingMore);
            Assert.True(vm.CanLoadMore);
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
        }
        finally
        {
            // 故意忽略取消并延后失败，覆盖旧请求 finally 不再拥有当前状态的情况。
            oldPage.TrySetException(new HttpRequestException("late old-page failure"));
            await loading;
        }
        Assert.False(vm.IsError);
        Assert.False(vm.IsLoadingMore);
        await vm.LoadMoreAsync();
        Assert.Equal(new[] { 2, 3 }, vm.Results.Select(entry => entry.Anime.ID));
        Assert.False(vm.CanLoadMore);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteFailureDoesNotSuppressSuccessfulLocalMatches(bool domainError)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var source = new SearchSource
        {
            Search = (_, _, _) => Task.FromException<(List<Anime>, int)>(domainError
                ? new BangumiApiException("offline") : new HttpRequestException("offline")),
        };
        var vm = NewViewModel(source, tracking);

        await vm.SearchAsync("作品");

        Assert.True(vm.IsError);
        Assert.False(vm.IsSearching);
        Assert.True(vm.HasLocalMatches);
        Assert.Equal("1 部", vm.LocalSummary);
        Assert.Equal(1, Assert.Single(vm.LocalMatches).Anime.ID);
    }

    [Fact]
    public async Task LocalMatchesArePublishedBeforeRemoteRequestCompletes()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var remote = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = NewViewModel(new SearchSource { Search = (_, _, _) => remote.Task }, tracking);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.HasLocalMatches) && vm.HasLocalMatches)
                localPublished.TrySetResult();
        };
        var search = vm.SearchAsync("作品");
        try
        {
            await localPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(search.IsCompleted);
            Assert.Equal(1, Assert.Single(vm.LocalMatches).Anime.ID);
        }
        finally
        {
            remote.TrySetResult(([], 0));
            await search;
        }
    }

    [Theory]
    [InlineData(AnimeTrackingStatus.None)]
    [InlineData(AnimeTrackingStatus.Blocked)]
    public async Task RefreshStatusesRemovesIneligibleLocalMatchesAndUpdatesCount(AnimeTrackingStatus status)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Watching);
        var vm = NewViewModel(new SearchSource(), tracking);
        await vm.SearchAsync("作品");
        Assert.Equal(2, vm.LocalMatches.Count);

        if (status == AnimeTrackingStatus.None)
            await tracking.RemoveStatusAsync(1);
        else
            await tracking.SetStatusAsync(1, status);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed);
        await vm.RefreshStatusesAsync();

        var remaining = Assert.Single(vm.LocalMatches);
        Assert.Equal(2, remaining.Anime.ID);
        Assert.Equal(AnimeTrackingStatus.Completed, remaining.Status);
        Assert.True(vm.HasLocalMatches);
        Assert.Equal("1 部", vm.LocalSummary);

        await tracking.RemoveStatusAsync(2);
        await vm.RefreshStatusesAsync();
        Assert.Empty(vm.LocalMatches);
        Assert.False(vm.HasLocalMatches);
        Assert.Equal("0 部", vm.LocalSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteErrorIsPublishedWhileLocalDetailsAreStillPending(bool domainError)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var detail = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = NewViewModel(new SearchSource
        {
            Detail = (_, _) => detail.Task,
            Search = (_, _, _) => Task.FromException<(List<Anime>, int)>(domainError
                ? new BangumiApiException("offline") : new HttpRequestException("offline")),
        }, tracking);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsError) && vm.IsError)
                errorPublished.TrySetResult();
        };

        var search = vm.SearchAsync("作品");
        try
        {
            await errorPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(search.IsCompleted);
            Assert.True(vm.IsSearching);
            Assert.False(vm.HasNoResults);
            Assert.Contains("offline", vm.ErrorMessage);
        }
        finally
        {
            detail.TrySetResult(Item(1));
            await search;
        }
        Assert.True(vm.IsError);
        Assert.Equal(1, Assert.Single(vm.LocalMatches).Anime.ID);
        Assert.False(vm.IsSearching);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReturnResumesOnlyInterruptedRemoteBranchAndIgnoresOldCompletion(bool oldFails)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var oldRemote = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRemote = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var localPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedRemoteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remoteCalls = 0;
        var detailCalls = 0;
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) =>
            {
                if (++remoteCalls == 1)
                {
                    remoteStarted.TrySetResult();
                    return oldRemote.Task;
                }
                resumedRemoteStarted.TrySetResult();
                return newRemote.Task;
            },
            Detail = (id, _) =>
            {
                Interlocked.Increment(ref detailCalls);
                return Task.FromResult<Anime?>(Item(id));
            },
        }, tracking);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.HasLocalMatches) && vm.HasLocalMatches)
                localPublished.TrySetResult();
        };
        var search = vm.SearchAsync("作品");
        Task resumed = Task.CompletedTask;
        try
        {
            await Task.WhenAll(localPublished.Task, remoteStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            vm.SetSortKey(SearchSortKey.Score);
            vm.CancelPendingSearch();
            await vm.RefreshStatusesAsync();
            Assert.False(vm.HasNoResults);
            Assert.Equal("", vm.SummaryText);
            Assert.Single(vm.LocalMatches);

            resumed = vm.ResumePendingSearchAsync();
            await resumedRemoteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.ResumePendingSearchAsync(); // 重复 Loaded 不再发起同一请求。
            Assert.Equal(2, remoteCalls);
            Assert.Equal(1, detailCalls);
            if (oldFails)
                oldRemote.TrySetException(new HttpRequestException("late failure"));
            else
                oldRemote.TrySetResult(([Item(99)], 1));
            await search;
            Assert.True(vm.IsSearching);
            Assert.False(vm.IsError);
            Assert.Empty(vm.Results);

            newRemote.TrySetResult(([Item(2)], 1));
            await resumed;
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(SearchSortKey.Score, vm.SortKey);
            Assert.Equal("作品", vm.Query);
            Assert.False(vm.IsSearching);
        }
        finally
        {
            oldRemote.TrySetResult(([], 0));
            newRemote.TrySetResult(([], 0));
            await Task.WhenAll(search, resumed);
        }
    }

    [Fact]
    public async Task ReturnResumesOnlyInterruptedLocalBranchAndPreservesRemoteResults()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var oldDetail = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remotePublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailCalls = 0;
        var remoteCalls = 0;
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) =>
            {
                ++remoteCalls;
                return Task.FromResult((new List<Anime> { Item(2) }, 1));
            },
            Detail = (id, _) =>
            {
                if (Interlocked.Increment(ref detailCalls) != 1)
                    return Task.FromResult<Anime?>(Item(id));
                detailStarted.TrySetResult();
                return oldDetail.Task;
            },
        }, tracking);
        vm.Results.CollectionChanged += (_, _) =>
        {
            if (vm.Results.Count == 1)
                remotePublished.TrySetResult();
        };
        var search = vm.SearchAsync("作品");
        try
        {
            await Task.WhenAll(detailStarted.Task, remotePublished.Task).WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelPendingSearch();
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
            await vm.RefreshStatusesAsync();
            await vm.ResumePendingSearchAsync();
            Assert.Equal(1, remoteCalls);
            Assert.Equal(2, detailCalls);
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(AnimeTrackingStatus.Completed, Assert.Single(vm.LocalMatches).Status);
        }
        finally
        {
            oldDetail.TrySetResult(Item(1));
            await search;
        }
        Assert.Equal(AnimeTrackingStatus.Completed, Assert.Single(vm.LocalMatches).Status);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task CompletedSearchAndLandingAreNotRepeatedOnReturn()
    {
        await RunProductionMigrationAsync();
        var calls = 0;
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) =>
            {
                ++calls;
                return Task.FromResult((new List<Anime> { Item(2) }, 1));
            },
        }, new TrackingService(DbFactory));
        await vm.ResumePendingSearchAsync();
        Assert.Equal(0, calls);
        await vm.SearchAsync("作品");
        vm.CancelPendingSearch();
        await vm.ResumePendingSearchAsync();
        Assert.Equal(1, calls);
        Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
        vm.ShowLanding();
        vm.CancelPendingSearch();
        await vm.ResumePendingSearchAsync();
        Assert.Equal(1, calls);
        Assert.True(vm.IsLanding);
    }

    [Fact]
    public async Task ReturnPreservesPaginationOffsetAndRestoresCancellableLoadMore()
    {
        await RunProductionMigrationAsync();
        var page = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<int>();
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, offset, token) =>
            {
                Assert.True(token.CanBeCanceled);
                calls.Add(offset);
                return offset == 0 ? Task.FromResult((new List<Anime> { Item(1) }, 2))
                    : calls.Count == 2 ? page.Task : Task.FromResult((new List<Anime> { Item(2) }, 2));
            },
        }, new TrackingService(DbFactory));
        await vm.SearchAsync("作品");
        var loading = vm.LoadMoreAsync();
        try
        {
            vm.CancelPendingSearch();
            Assert.False(vm.CanLoadMore);
            await vm.ResumePendingSearchAsync();
            Assert.True(vm.CanLoadMore);
            await vm.LoadMoreAsync();
            Assert.Equal(new[] { 0, 1, 1 }, calls);
        }
        finally
        {
            page.TrySetResult(([Item(99)], 2));
            await loading;
        }
        Assert.Equal(new[] { 1, 2 }, vm.Results.Select(entry => entry.Anime.ID));
        Assert.False(vm.IsLoadingMore);
    }

    [Fact]
    public async Task ReturnAfterRemoteFailureResumesLocalBranchWithoutAutomaticallyRetryingRemote()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var oldDetail = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var errorPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailCalls = 0;
        var remoteCalls = 0;
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) =>
            {
                ++remoteCalls;
                return Task.FromException<(List<Anime>, int)>(new HttpRequestException("offline"));
            },
            Detail = (id, _) =>
            {
                if (Interlocked.Increment(ref detailCalls) != 1)
                    return Task.FromResult<Anime?>(Item(id));
                detailStarted.TrySetResult();
                return oldDetail.Task;
            },
        }, tracking);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsError) && vm.IsError)
                errorPublished.TrySetResult();
        };
        var search = vm.SearchAsync("作品");
        try
        {
            await Task.WhenAll(detailStarted.Task, errorPublished.Task).WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelPendingSearch();
            await vm.ResumePendingSearchAsync();
            Assert.Equal(1, remoteCalls);
            Assert.Equal(2, detailCalls);
            Assert.Single(vm.LocalMatches);
            Assert.True(vm.IsError);
            Assert.Contains("offline", vm.ErrorMessage);
        }
        finally
        {
            oldDetail.TrySetResult(Item(1));
            await search;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewQueryOrLandingDiscardsInterruptedInitialSearch(bool returnToLanding)
    {
        await RunProductionMigrationAsync();
        var oldRemote = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var vm = NewViewModel(new SearchSource
        {
            Search = (query, _, _) =>
            {
                calls.Add(query);
                if (query != "old")
                    return Task.FromResult((new List<Anime> { Item(2) }, 1));
                oldStarted.TrySetResult();
                return oldRemote.Task;
            },
        }, new TrackingService(DbFactory));
        var search = vm.SearchAsync("old");
        try
        {
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelPendingSearch();
            if (returnToLanding)
                vm.ShowLanding();
            else
                await vm.SearchAsync("new");
            await vm.ResumePendingSearchAsync();
        }
        finally
        {
            oldRemote.TrySetException(new HttpRequestException("late old search failure"));
            await search;
        }

        Assert.False(vm.IsError);
        Assert.False(vm.IsSearching);
        Assert.Equal(returnToLanding, vm.IsLanding);
        Assert.Equal(returnToLanding ? new[] { "old" } : new[] { "old", "new" }, calls);
        if (!returnToLanding)
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
    }

    private GlobalSearchViewModel NewViewModel(IAnimeDataSource source, TrackingService tracking)
        => new(source, tracking, new LocalSearchService(tracking, source), new SavedTagService(DbFactory));

    private static Anime Item(int id) => new(id, $"作品{id}", null, [], null, null, "", 2026, 1);

    private sealed class SearchSource : IAnimeDataSource
    {
        public Func<string, int, CancellationToken, Task<(List<Anime>, int)>> Search { get; init; }
            = (_, _, _) => Task.FromResult((new List<Anime>(), 0));
        public Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(Item(id));

        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Search(keyword, offset, ct);
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => Detail(animeID, ct);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
            => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
