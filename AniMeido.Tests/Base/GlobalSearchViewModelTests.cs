using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class GlobalSearchViewModelTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    public GlobalSearchViewModelTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
    }

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

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
        Task currentSearch = Task.CompletedTask;
        try
        {
            Assert.True(vm.IsLoadingMore);
            currentSearch = vm.SearchAsync("new");
            await currentSearch.WaitAsync(Budget);
            Assert.False(vm.IsLoadingMore);
            Assert.True(vm.CanLoadMore);
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
        }
        finally
        {
            // 故意忽略取消并延后失败，覆盖旧请求 finally 不再拥有当前状态的情况。
            oldPage.TrySetException(new HttpRequestException("late old-page failure"));
            await Task.WhenAll(loading, currentSearch).WaitAsync(Budget);
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
                ? new MetadataServiceException("offline") : new HttpRequestException("offline")),
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
            await localPublished.Task.WaitAsync(Budget);
            Assert.False(search.IsCompleted);
            Assert.Equal(1, Assert.Single(vm.LocalMatches).Anime.ID);
        }
        finally
        {
            remote.TrySetResult(([], 0));
            await search.WaitAsync(Budget);
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
                ? new MetadataServiceException("offline") : new HttpRequestException("offline")),
        }, tracking);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsError) && vm.IsError)
                errorPublished.TrySetResult();
        };

        var search = vm.SearchAsync("作品");
        try
        {
            await errorPublished.Task.WaitAsync(Budget);
            Assert.False(search.IsCompleted);
            Assert.True(vm.IsSearching);
            Assert.False(vm.HasNoResults);
            Assert.Contains("offline", vm.ErrorMessage);
        }
        finally
        {
            detail.TrySetResult(Item(1));
            await search.WaitAsync(Budget);
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
        Task repeatedResume = Task.CompletedTask;
        try
        {
            await Task.WhenAll(localPublished.Task, remoteStarted.Task).WaitAsync(Budget);
            vm.SetSortKey(SearchSortKey.Score);
            vm.CancelPendingSearch();
            await vm.RefreshStatusesAsync();
            Assert.False(vm.HasNoResults);
            Assert.Equal("", vm.SummaryText);
            Assert.Single(vm.LocalMatches);

            resumed = vm.ResumePendingSearchAsync();
            await resumedRemoteStarted.Task.WaitAsync(Budget);
            repeatedResume = vm.ResumePendingSearchAsync();
            await repeatedResume.WaitAsync(Budget); // 重复 Loaded 不再发起同一请求。
            Assert.Equal(2, remoteCalls);
            Assert.Equal(1, detailCalls);
            if (oldFails)
                oldRemote.TrySetException(new HttpRequestException("late failure"));
            else
                oldRemote.TrySetResult(([Item(99)], 1));
            await search.WaitAsync(Budget);
            Assert.True(vm.IsSearching);
            Assert.False(vm.IsError);
            Assert.Empty(vm.Results);

            newRemote.TrySetResult(([Item(2)], 1));
            await resumed.WaitAsync(Budget);
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(SearchSortKey.Score, vm.SortKey);
            Assert.Equal("作品", vm.Query);
            Assert.False(vm.IsSearching);
        }
        finally
        {
            oldRemote.TrySetResult(([], 0));
            newRemote.TrySetResult(([], 0));
            await Task.WhenAll(search, resumed, repeatedResume).WaitAsync(Budget);
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
        Task resumed = Task.CompletedTask;
        try
        {
            await Task.WhenAll(detailStarted.Task, remotePublished.Task).WaitAsync(Budget);
            vm.CancelPendingSearch();
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
            await vm.RefreshStatusesAsync();
            resumed = vm.ResumePendingSearchAsync();
            await resumed.WaitAsync(Budget);
            Assert.Equal(1, remoteCalls);
            Assert.Equal(2, detailCalls);
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(AnimeTrackingStatus.Completed, Assert.Single(vm.LocalMatches).Status);
        }
        finally
        {
            oldDetail.TrySetResult(Item(1));
            await Task.WhenAll(search, resumed).WaitAsync(Budget);
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
        Task resumed = Task.CompletedTask;
        Task nextPage = Task.CompletedTask;
        try
        {
            vm.CancelPendingSearch();
            Assert.False(vm.CanLoadMore);
            resumed = vm.ResumePendingSearchAsync();
            await resumed.WaitAsync(Budget);
            Assert.True(vm.CanLoadMore);
            nextPage = vm.LoadMoreAsync();
            await nextPage.WaitAsync(Budget);
            Assert.Equal(new[] { 0, 1, 1 }, calls);
        }
        finally
        {
            page.TrySetResult(([Item(99)], 2));
            await Task.WhenAll(loading, resumed, nextPage).WaitAsync(Budget);
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
        Task resumed = Task.CompletedTask;
        try
        {
            await Task.WhenAll(detailStarted.Task, errorPublished.Task).WaitAsync(Budget);
            vm.CancelPendingSearch();
            resumed = vm.ResumePendingSearchAsync();
            await resumed.WaitAsync(Budget);
            Assert.Equal(1, remoteCalls);
            Assert.Equal(2, detailCalls);
            Assert.Single(vm.LocalMatches);
            Assert.True(vm.IsError);
            Assert.Contains("offline", vm.ErrorMessage);
        }
        finally
        {
            oldDetail.TrySetResult(Item(1));
            await Task.WhenAll(search, resumed).WaitAsync(Budget);
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
        Task currentSearch = Task.CompletedTask;
        Task resumed = Task.CompletedTask;
        try
        {
            await oldStarted.Task.WaitAsync(Budget);
            vm.CancelPendingSearch();
            if (returnToLanding)
                vm.ShowLanding();
            else
            {
                currentSearch = vm.SearchAsync("new");
                await currentSearch.WaitAsync(Budget);
            }
            resumed = vm.ResumePendingSearchAsync();
            await resumed.WaitAsync(Budget);
        }
        finally
        {
            oldRemote.TrySetException(new HttpRequestException("late old search failure"));
            await Task.WhenAll(search, currentSearch, resumed).WaitAsync(Budget);
        }

        Assert.False(vm.IsError);
        Assert.False(vm.IsSearching);
        Assert.Equal(returnToLanding, vm.IsLanding);
        Assert.Equal(returnToLanding ? new[] { "old" } : new[] { "old", "new" }, calls);
        if (!returnToLanding)
            Assert.Equal(2, Assert.Single(vm.Results).Anime.ID);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CompletedReturn_RestoresPaginationWithoutReadingSqliteOrReprojectingResults()
    {
        await RunProductionMigrationAsync();
        var offsets = new List<int>();
        CancellationToken initialToken = default;
        CancellationToken moreToken = default;
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, offset, token) =>
            {
                offsets.Add(offset);
                Assert.True(token.CanBeCanceled);
                if (offset == 0)
                {
                    initialToken = token;
                    return Task.FromResult((new List<Anime>
                    {
                        Item(1) with { MediaFormat = AnimeMediaFormat.Television, Score = 5 },
                        Item(2) with { MediaFormat = AnimeMediaFormat.Television, Score = 7 },
                        Item(3) with { MediaFormat = AnimeMediaFormat.Movie, Score = 10 },
                    }, 4));
                }

                moreToken = token;
                return Task.FromResult((new List<Anime>
                {
                    Item(4) with { MediaFormat = AnimeMediaFormat.Television, Score = 9 },
                }, 4));
            },
        }, new TrackingService(DbFactory));
        await vm.SearchAsync("作品");
        vm.SelectFormat(AnimeMediaFormat.Television);
        vm.SetSortKey(SearchSortKey.Score);
        Assert.Equal([2, 1], vm.Results.Select(entry => entry.Anime.ID));
        vm.CancelPendingSearch();
        Assert.False(vm.CanLoadMore);
        Assert.True(initialToken.IsCancellationRequested);
        var chips = vm.FormatChips;
        var results = vm.Results.ToArray();
        using var blocker = await LockSearchDatabaseAsync();
        Task? resumed = null;

        try
        {
            resumed = vm.ResumePendingSearchAsync();
            Assert.Same(chips, vm.FormatChips);
            await resumed.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(vm.CanLoadMore);
            Assert.Equal(results, vm.Results);
            Assert.Equal(AnimeMediaFormat.Television, vm.SelectedFormat);
            Assert.Equal(SearchSortKey.Score, vm.SortKey);
            Assert.Equal([0], offsets);
        }
        finally
        {
            blocker.Dispose();
            if (resumed is not null)
            {
                await resumed.WaitAsync(Budget);
            }
        }

        await vm.LoadMoreAsync().WaitAsync(Budget);

        Assert.Equal([0, 3], offsets);
        Assert.True(moreToken.CanBeCanceled);
        Assert.False(moreToken.IsCancellationRequested);
        Assert.NotEqual(initialToken, moreToken);
        Assert.Equal([4, 2, 1], vm.Results.Select(entry => entry.Anime.ID));
        Assert.False(vm.CanLoadMore);
    }

    [Fact]
    public async Task Landing_AnOlderReadCannotReplaceTheLatestPublishedRecentSearches()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveRecentSearchesAsync(["old"]);
        var vm = NewViewModel(new SearchSource(), tracking);
        var (context, originalLoad) = await PauseLandingBeforePublicationAsync(vm);

        try
        {
            await _featureState.SaveRecentSearchesAsync(["new"]);
            await vm.LoadLandingAsync().WaitAsync(Budget);
            Assert.Equal(["new"], vm.RecentSearches);

            context.Resume();
            await originalLoad.WaitAsync(Budget);

            Assert.Equal(["new"], vm.RecentSearches);
        }
        finally
        {
            context.Resume();
            await originalLoad.WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("search")]
    [InlineData("clear recent")]
    [InlineData("leave")]
    public async Task Landing_AnOlderReadCannotPublishAfterThePageStateChanges(string action)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveRecentSearchesAsync(["old"]);
        var vm = NewViewModel(new SearchSource(), tracking);
        var (context, originalLoad) = await PauseLandingBeforePublicationAsync(vm);

        try
        {
            switch (action)
            {
                case "search":
                    await vm.SearchAsync("new").WaitAsync(Budget);
                    Assert.Equal(["new"], vm.RecentSearches);
                    break;
                case "clear recent":
                    await vm.ClearRecentSearchesAsync().WaitAsync(Budget);
                    Assert.Empty(vm.RecentSearches);
                    Assert.Empty(await _featureState.LoadRecentSearchesAsync());
                    break;
                case "leave":
                    vm.CancelPendingSearch();
                    Assert.Empty(vm.RecentSearches);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            context.Resume();
            await originalLoad.WaitAsync(Budget);

            if (action == "search")
            {
                Assert.Equal(["new"], vm.RecentSearches);
                Assert.Equal("new", vm.Query);
                Assert.True(vm.HasSearched);
            }
            else
            {
                Assert.Empty(vm.RecentSearches);
            }
        }
        finally
        {
            context.Resume();
            await originalLoad.WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Landing_AnOlderSqlFailureCannotEscapeAfterLeavingOrStartingAnotherSearch(bool searchAgain)
    {
        await RunProductionMigrationAsync();
        await DropSearchConfigAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) => Task.FromResult((new List<Anime> { Item(42) }, 1)),
        }, tracking);
        var (context, originalLoad) = await PauseLandingBeforePublicationAsync(vm);

        try
        {
            if (searchAgain)
            {
                await vm.SearchAsync("new").WaitAsync(Budget);
                Assert.Equal(42, Assert.Single(vm.Results).Anime.ID);
            }
            else
            {
                vm.CancelPendingSearch();
            }

            // 真正的 SQLite 失败已排队；旧版本失效后才允许它回到 ViewModel。
            context.Resume();
            await originalLoad.WaitAsync(Budget);

            Assert.False(vm.IsError);
            Assert.Null(vm.ErrorMessage);
            if (searchAgain)
            {
                Assert.Equal("new", vm.Query);
                Assert.Equal(42, Assert.Single(vm.Results).Anime.ID);
            }
            else
            {
                Assert.Empty(vm.RecentSearches);
                Assert.Empty(vm.SavedTags);
            }
        }
        finally
        {
            context.Resume();
            await originalLoad.WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Landing_CurrentSqlFailureStillPropagatesToItsCaller()
    {
        await RunProductionMigrationAsync();
        await DropSearchConfigAsync();
        var vm = NewViewModel(new SearchSource(), new TrackingService(DbFactory));

        var failure = await Assert.ThrowsAsync<SqliteException>(vm.LoadLandingAsync);

        Assert.Equal(1, failure.SqliteErrorCode);
        Assert.Contains("config", failure.Message);
        Assert.False(vm.IsError);
        Assert.Null(vm.ErrorMessage);
    }

    private async Task DropSearchConfigAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE config";
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(PausedUiContext Context, Task Loading)> PauseLandingBeforePublicationAsync(
        GlobalSearchViewModel vm)
    {
        var context = new PausedUiContext();
        using var blocker = await LockSearchDatabaseAsync();

        Task? loading = null;

        try
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                loading = vm.LoadLandingAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            Assert.False(loading.IsCompleted);

            blocker.Dispose();
            await context.FirstPost.WaitAsync(Budget);
            Assert.False(loading.IsCompleted);
            return (context, loading);
        }
        catch
        {
            blocker.Dispose();
            context.Resume();
            if (loading is not null)
            {
                await loading.WaitAsync(Budget);
            }
            throw;
        }
    }

    private async Task<SqliteConnection> LockSearchDatabaseAsync()
    {
        using (var pooled = DbFactory.CreateConnection())
        {
            SqliteConnection.ClearPool(pooled);
        }
        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await blocker.OpenAsync();
            using var command = blocker.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return blocker;
        }
        catch
        {
            blocker.Dispose();
            throw;
        }
    }

    private GlobalSearchViewModel NewViewModel(IAnimeDataSource source, TrackingService tracking, ILogger? logger = null)
        => new(source, tracking, _featureState,
            new LocalSearchService(tracking, source, NullLogger<LocalSearchService>.Instance),
            new SavedTagService(DbFactory), logger ?? NullLogger.Instance);

    private static Anime Item(int id) => new(id, $"作品{id}", null, [], null, null, "", 2026, 1);

    private sealed class SearchSource : IAnimeDataSource
    {
        public Func<string, int, CancellationToken, Task<(List<Anime>, int)>> Search { get; init; }
            = (_, _, _) => Task.FromResult((new List<Anime>(), 0));
        public Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(Item(id));

        public async Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
        {
            var (results, total) = await Search(keyword, offset, ct);
            return (results, total, offset + results.Count);
        }
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
