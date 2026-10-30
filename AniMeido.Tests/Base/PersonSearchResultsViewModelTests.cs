using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed partial class PersonSearchResultsViewModelTests : DbTestBase
{
    [Fact]
    public async Task Load_KeepsFirstPositiveWorkOrderAndFiltersBlockedDetails()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Blocked);
        var requested = new List<int>();
        var source = new PersonSource
        {
            Works = (_, _) => Task.FromResult(new List<PersonWork>
            {
                Work(3), Work(0), Work(-1), Work(2), Work(3), Work(1),
            }),
            Detail = (id, _) =>
            {
                requested.Add(id);
                return Task.FromResult<Anime?>(Item(id));
            },
        };
        var vm = new PersonSearchResultsViewModel(source, tracking, NullLogger.Instance);

        await vm.LoadAsync(7, "测试声优");

        Assert.Equal("声优作品：测试声优", vm.Title);
        Assert.Equal(new[] { 3, 2, 1 }, requested);
        Assert.Equal(new[] { 3, 1 }, vm.Results.Select(anime => anime.ID));
        Assert.Equal("参与 3 部作品 · 显示 2 部", vm.ResultText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Load_PublishesFourWorkBatchesInInputOrderAndReportsProgress()
    {
        await RunProductionMigrationAsync();
        var firstBatch = new TaskCompletionSource<Anime?>[4];
        for (var index = 0; index < firstBatch.Length; index++)
            firstBatch[index] = NewCompletion<Anime?>();
        var firstBatchStarted = NewCompletion();
        var secondBatchStarted = NewCompletion();
        var last = NewCompletion<Anime?>();
        var requested = new List<int>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromResult(Enumerable.Range(1, 5).Select(Work).ToList()),
            Detail = (id, _) =>
            {
                requested.Add(id);
                if (id == 4)
                    firstBatchStarted.TrySetResult();
                if (id == 5)
                    secondBatchStarted.TrySetResult();
                return id <= 4 ? firstBatch[id - 1].Task : last.Task;
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var loading = vm.LoadAsync(7, "声优");
        try
        {
            await firstBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 2, 3, 4 }, requested);
            Assert.Empty(vm.Results);
            Assert.False(vm.IsLoading);
            Assert.Equal("正在加载 0/5 部作品", vm.ResultText);
            firstBatch[3].SetResult(Item(4));
            firstBatch[1].SetResult(Item(2));
            firstBatch[0].SetResult(Item(1));
            Assert.False(secondBatchStarted.Task.IsCompleted);
            firstBatch[2].SetResult(Item(3));
            await secondBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Results.Select(anime => anime.ID));
            Assert.Equal("正在加载 4/5 部作品", vm.ResultText);
        }
        finally
        {
            for (var index = 0; index < firstBatch.Length; index++)
                firstBatch[index].TrySetResult(Item(index + 1));
            last.TrySetResult(Item(5));
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, vm.Results.Select(anime => anime.ID));
        Assert.Equal("参与 5 部作品 · 显示 5 部", vm.ResultText);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("json")]
    public async Task DetailFailure_UsesWorkSnapshotWhileNullDetailsStayAbsent(string failure)
    {
        await RunProductionMigrationAsync();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromResult(new List<PersonWork>
            {
                new(1, "名单标题", "配音", "https://example.invalid/cover", 8.2), Work(2),
            }),
            Detail = (id, _) => id == 1
                ? Task.FromException<Anime?>(Failure(failure))
                : Task.FromResult<Anime?>(null),
        }, new TrackingService(DbFactory), NullLogger.Instance);

        await vm.LoadAsync(7, "声优");

        var fallback = Assert.Single(vm.Results);
        Assert.Equal(1, fallback.ID);
        Assert.Equal("名单标题", fallback.Title);
        Assert.Equal("配音", fallback.Description);
        Assert.Equal("https://example.invalid/cover", fallback.CoverURL);
        Assert.Null(fallback.Score);
        Assert.Equal(0, fallback.SeasonYear);
        Assert.Equal("参与 2 部作品 · 显示 1 部", vm.ResultText);
    }

    [Theory]
    [InlineData("http", "加载失败：offline")]
    [InlineData("metadata", "数据源请求失败：offline")]
    [InlineData("json", "数据解析失败：offline")]
    public async Task WorkListFailure_ReportsCurrentErrorAndStopsOverlay(string failure, string expected)
    {
        await RunProductionMigrationAsync();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromException<List<PersonWork>>(Failure(failure)),
        }, new TrackingService(DbFactory), NullLogger.Instance);

        await vm.LoadAsync(7, "声优");

        Assert.Empty(vm.Results);
        Assert.Equal(expected, vm.ResultText);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task RefreshBlocked_RemovesNewlyBlockedResultsWithoutRequestingWorksAgain()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var listCalls = 0;
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                return Task.FromResult(new List<PersonWork> { Work(1), Work(2) });
            },
        }, tracking, NullLogger.Instance);
        await vm.LoadAsync(7, "声优");
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Blocked);

        await vm.ResumeAsync();

        Assert.Equal(2, Assert.Single(vm.Results).ID);
        Assert.Equal("显示 1 部作品", vm.ResultText);
        Assert.Equal(1, listCalls);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Blocked);
        await vm.ResumeAsync();
        Assert.Empty(vm.Results);
        Assert.Equal("未找到相关作品", vm.ResultText);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("json")]
    public async Task ReplacedRequest_IgnoringCancellationCannotPublishResultsErrorOrStopCurrentOverlay(string outcome)
    {
        await RunProductionMigrationAsync();
        var oldStarted = NewCompletion();
        var currentStarted = NewCompletion();
        var oldWorks = NewCompletion<List<PersonWork>>();
        var currentWorks = NewCompletion<List<PersonWork>>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (id, _) =>
            {
                (id == 1 ? oldStarted : currentStarted).TrySetResult();
                return id == 1 ? oldWorks.Task : currentWorks.Task;
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var oldLoad = vm.LoadAsync(1, "旧声优");
        await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var currentLoad = vm.LoadAsync(2, "新声优");
        try
        {
            await currentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var text = vm.ResultText;
            if (outcome == "success")
                oldWorks.SetResult([Work(1)]);
            else
                oldWorks.SetException(Failure(outcome));
            await oldLoad.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("声优作品：新声优", vm.Title);
            Assert.Equal(text, vm.ResultText);
            Assert.True(vm.IsLoading);
            Assert.Empty(vm.Results);
        }
        finally
        {
            oldWorks.TrySetResult([Work(1)]);
            currentWorks.TrySetResult([Work(2)]);
            await Task.WhenAll(oldLoad, currentLoad).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, Assert.Single(vm.Results).ID);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task CancelLoad_DiscardsLateDetailBatchWithoutClearingPublishedResults()
    {
        await RunProductionMigrationAsync();
        var started = NewCompletion();
        var last = NewCompletion<Anime?>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromResult(Enumerable.Range(1, 5).Select(Work).ToList()),
            Detail = (id, _) =>
            {
                if (id != 5)
                    return Task.FromResult<Anime?>(Item(id));
                started.TrySetResult();
                return last.Task;
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var loading = vm.LoadAsync(7, "声优");
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelLoad();
            var text = vm.ResultText;
            last.SetResult(Item(5));
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Results.Select(anime => anime.ID));
            Assert.Equal(text, vm.ResultText);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            last.TrySetResult(Item(5));
            await loading.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task ReturnAfterCompletion_OnlyRefreshesBlockedWithoutReloadingDetails()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var listCalls = 0;
        var details = new List<int>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                return Task.FromResult(new List<PersonWork> { Work(1), Work(2) });
            },
            Detail = (id, _) =>
            {
                details.Add(id);
                return Task.FromResult<Anime?>(Item(id));
            },
        }, tracking, NullLogger.Instance);
        await vm.LoadAsync(7, "声优");
        vm.CancelLoad();
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Blocked);

        await vm.ResumeAsync();
        await vm.ResumeAsync();

        Assert.Equal(2, Assert.Single(vm.Results).ID);
        Assert.Equal("显示 1 部作品", vm.ResultText);
        Assert.Equal(1, listCalls);
        Assert.Equal(new[] { 1, 2 }, details);
    }

    [Theory]
    [InlineData("success")]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("json")]
    public async Task ReturnAfterPartialBatch_ResumesOnlyUnpublishedWorksAndIgnoresOldCompletion(string outcome)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var oldBatchStarted = NewCompletion();
        var resumedBatchStarted = NewCompletion();
        var oldDetail = NewCompletion<Anime?>();
        var resumedDetail = NewCompletion<Anime?>();
        var listCalls = 0;
        var details = new List<int>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                return Task.FromResult(Enumerable.Range(1, 6).Select(Work).ToList());
            },
            Detail = (id, _) =>
            {
                details.Add(id);
                if (id != 5)
                    return Task.FromResult<Anime?>(Item(id));
                if (details.Count(value => value == 5) == 1)
                {
                    oldBatchStarted.TrySetResult();
                    return oldDetail.Task;
                }
                resumedBatchStarted.TrySetResult();
                return resumedDetail.Task;
            },
        }, tracking, NullLogger.Instance);
        var original = vm.LoadAsync(7, "声优");
        Task resumed = Task.CompletedTask;
        try
        {
            await oldBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 2, 3, 4 }, vm.Results.Select(anime => anime.ID));
            vm.CancelLoad();
            await tracking.SetStatusAsync(2, AnimeTrackingStatus.Blocked);
            await tracking.SetStatusAsync(6, AnimeTrackingStatus.Blocked);
            resumed = vm.ResumeAsync();
            await resumedBatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var progress = vm.ResultText;
            if (outcome == "success")
                oldDetail.SetResult(Item(500));
            else
                oldDetail.SetException(Failure(outcome));
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { 1, 3, 4 }, vm.Results.Select(anime => anime.ID));
            Assert.Equal(progress, vm.ResultText);
            Assert.False(resumed.IsCompleted);
        }
        finally
        {
            oldDetail.TrySetResult(Item(500));
            resumedDetail.TrySetResult(Item(5));
            await Task.WhenAll(original, resumed).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(new[] { 1, 3, 4, 5 }, vm.Results.Select(anime => anime.ID));
        Assert.Equal("参与 6 部作品 · 显示 4 部", vm.ResultText);
        Assert.Equal(1, listCalls);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 5, 6 }, details);
    }

    [Fact]
    public async Task ReturnBeforeListArrives_RestartsUnfinishedListAndRejectsOldResult()
    {
        await RunProductionMigrationAsync();
        var oldStarted = NewCompletion();
        var resumedStarted = NewCompletion();
        var oldWorks = NewCompletion<List<PersonWork>>();
        var resumedWorks = NewCompletion<List<PersonWork>>();
        var listCalls = 0;
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                (listCalls == 1 ? oldStarted : resumedStarted).TrySetResult();
                return listCalls == 1 ? oldWorks.Task : resumedWorks.Task;
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var original = vm.LoadAsync(7, "声优");
        Task resumed = Task.CompletedTask;
        try
        {
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelLoad();
            resumed = vm.ResumeAsync();
            await resumedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(vm.IsLoading);
            oldWorks.SetResult([Work(1)]);
            await original.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(vm.Results);
            Assert.True(vm.IsLoading);
        }
        finally
        {
            oldWorks.TrySetResult([Work(1)]);
            resumedWorks.TrySetResult([Work(2)]);
            await Task.WhenAll(original, resumed).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(2, Assert.Single(vm.Results).ID);
        Assert.Equal(2, listCalls);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task LoadedWhileInitialLoadRuns_DoesNotStartAnotherListOrDetailRequest()
    {
        await RunProductionMigrationAsync();
        var started = NewCompletion();
        var works = NewCompletion<List<PersonWork>>();
        var calls = 0;
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                calls++;
                started.TrySetResult();
                return works.Task;
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var resultUpdates = new List<string>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.ResultText))
                resultUpdates.Add(vm.ResultText);
        };
        var loading = vm.LoadAsync(7, "声优");
        Task loaded = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            loaded = vm.ResumeAsync();
            Assert.Equal(1, calls);
        }
        finally
        {
            works.TrySetResult([Work(1)]);
            await Task.WhenAll(loading, loaded).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, calls);
        Assert.Equal(1, Assert.Single(vm.Results).ID);
        Assert.DoesNotContain(resultUpdates, text => text == "未找到相关作品"
            || text.StartsWith("显示 ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedReturns_KeepPublishedBatchesAndCompleteRemainingWorksOnce()
    {
        await RunProductionMigrationAsync();
        var firstInterruptedBatch = NewCompletion();
        var secondInterruptedBatch = NewCompletion();
        var oldFifth = NewCompletion<Anime?>();
        var oldNinth = NewCompletion<Anime?>();
        var listCalls = 0;
        var details = new ConcurrentQueue<int>();
        var fifthCalls = 0;
        var eighthCalls = 0;
        var ninthCalls = 0;
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                return Task.FromResult(Enumerable.Range(1, 9).Select(Work).ToList());
            },
            Detail = (id, _) =>
            {
                details.Enqueue(id);
                // 等旧批四个请求都登记后再取消，不能在第五部刚进入时抢先恢复新批。
                if (id == 8 && Interlocked.Increment(ref eighthCalls) == 1)
                    firstInterruptedBatch.TrySetResult();
                if (id == 5 && Interlocked.Increment(ref fifthCalls) == 1)
                    return oldFifth.Task;
                if (id == 9 && Interlocked.Increment(ref ninthCalls) == 1)
                {
                    secondInterruptedBatch.TrySetResult();
                    return oldNinth.Task;
                }
                return Task.FromResult<Anime?>(Item(id));
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        var first = vm.LoadAsync(7, "声优");
        Task second = Task.CompletedTask;
        Task third = Task.CompletedTask;
        try
        {
            await firstInterruptedBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.CancelLoad();
            second = vm.ResumeAsync();
            await secondInterruptedBatch.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Enumerable.Range(1, 8), vm.Results.Select(anime => anime.ID));
            vm.CancelLoad();
            third = vm.ResumeAsync();
            await third.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            oldFifth.TrySetResult(Item(500));
            oldNinth.TrySetResult(Item(900));
            await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(Enumerable.Range(1, 9), vm.Results.Select(anime => anime.ID));
        Assert.Equal("参与 9 部作品 · 显示 9 部", vm.ResultText);
        Assert.Equal(1, listCalls);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 5, 6, 7, 8, 9, 9 }, details);
    }

    [Theory]
    [InlineData("http", "加载失败：offline")]
    [InlineData("metadata", "数据源请求失败：offline")]
    [InlineData("json", "数据解析失败：offline")]
    public async Task ReturnAfterListFailure_RetriesUnfinishedRequestAndReplacesError(string failure, string expected)
    {
        await RunProductionMigrationAsync();
        var listCalls = 0;
        var details = new List<int>();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                listCalls++;
                return listCalls == 1
                    ? Task.FromException<List<PersonWork>>(Failure(failure))
                    : Task.FromResult(new List<PersonWork> { Work(1), Work(2) });
            },
            Detail = (id, _) =>
            {
                details.Add(id);
                return Task.FromResult<Anime?>(Item(id));
            },
        }, new TrackingService(DbFactory), NullLogger.Instance);
        await vm.LoadAsync(7, "声优");
        Assert.Equal(expected, vm.ResultText);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Results);
        vm.CancelLoad();

        await vm.ResumeAsync();

        Assert.Equal(2, listCalls);
        Assert.Equal(new[] { 1, 2 }, details);
        Assert.Equal(new[] { 1, 2 }, vm.Results.Select(anime => anime.ID));
        Assert.Equal("参与 2 部作品 · 显示 2 部", vm.ResultText);
        Assert.False(vm.IsLoading);
    }

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static PersonWork Work(int id) => new(id, $"名单{id}", "配音");

    private static Anime Item(int id) => new(id, $"作品{id}", null, [], null, null, "", 2026, 1);

    private static Exception Failure(string kind) => kind switch
    {
        "http" => new HttpRequestException("offline"),
        "metadata" => new MetadataServiceException("offline"),
        "json" => new JsonException("offline"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private sealed class PersonSource : IAnimeDataSource
    {
        public Func<int, CancellationToken, Task<List<PersonWork>>> Works { get; init; }
            = (_, _) => Task.FromResult(new List<PersonWork>());

        public Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(Item(id));

        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Works(personId, ct);
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Detail(animeID, ct);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
