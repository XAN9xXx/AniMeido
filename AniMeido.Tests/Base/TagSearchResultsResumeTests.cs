using AniMeido.Contracts.Models;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.ViewModels;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class TagSearchResultsResumeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Resume_CompletedResultsKeepTheSecondPageWithoutRepeatingTheQuery()
    {
        var fixture = new QueryFixture(45);
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", 2023, 2026);
        vm.NextPage();
        var items = vm.Items;

        vm.Suspend();
        await vm.ResumeAsync();

        Assert.Equal(20, vm.CurrentOffset);
        Assert.Equal(Enumerable.Range(21, 20), vm.Items.Select(item => item.ID));
        Assert.Equal("2 / 3", vm.PageText);
        Assert.Equal(45, vm.TotalCount);
        Assert.Same(items, vm.Items);
        Assert.Equal(1, fixture.Queries);
        Assert.Equal(2, fixture.BlockedReads);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Resume_FilteredResultsClampOnlyTheNowMissingPageToTheLastValidPage()
    {
        var fixture = new QueryFixture(65);
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", 2023, 2026);
        vm.NextPage();
        vm.NextPage();
        vm.NextPage();
        Assert.Equal(60, vm.CurrentOffset);
        fixture.Blocked = Enumerable.Range(1, 20).ToHashSet();

        vm.Suspend();
        await vm.ResumeAsync();

        Assert.Equal(45, vm.TotalCount);
        Assert.Equal(40, vm.CurrentOffset);
        Assert.Equal(new[] { 61, 62, 63, 64, 65 }, vm.Items.Select(item => item.ID));
        Assert.Equal("3 / 3", vm.PageText);
        Assert.True(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.Equal(1, fixture.Queries);
    }

    [Fact]
    public async Task Resume_AllResultsBlockedClearsTheOldOffsetAndPagingState()
    {
        var fixture = new QueryFixture(45);
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", 2023, 2026);
        vm.NextPage();
        vm.NextPage();
        fixture.Blocked = Enumerable.Range(1, 45).ToHashSet();

        vm.Suspend();
        await vm.ResumeAsync();

        Assert.Empty(vm.Items);
        Assert.Equal(0, vm.TotalCount);
        Assert.Equal(0, vm.CurrentOffset);
        Assert.Equal("未找到相关番剧", vm.ResultText);
        Assert.False(vm.HasPages);
        Assert.False(vm.CanGoPrevious);
        Assert.False(vm.CanGoNext);
        Assert.False(vm.IsLoading);
        Assert.Equal(1, fixture.Queries);
    }

    [Fact]
    public async Task Resume_InterruptedQueryReloadsAndLateOldResultCannotFinishTheNewLoad()
    {
        var firstStarted = Signal();
        var secondStarted = Signal();
        var firstRelease = ResultsGate();
        var secondRelease = ResultsGate();
        var queries = 0;
        var vm = new TagSearchResultsViewModel((_, _, _, _) =>
        {
            if (++queries == 1)
            {
                firstStarted.TrySetResult();
                // 模拟已经发出、取消后仍会返回的旧请求。
                return firstRelease.Task;
            }
            secondStarted.TrySetResult();
            return secondRelease.Task;
        }, () => Task.FromResult(new HashSet<int>()), NullLogger.Instance);
        var first = vm.InitializeAsync("科幻", 2023, 2026);
        Task? resumed = null;
        try
        {
            await firstStarted.Task.WaitAsync(Budget);
            vm.Suspend();
            resumed = vm.ResumeAsync();
            Assert.Equal(2, queries);
            await secondStarted.Task.WaitAsync(Budget);
            Assert.True(vm.IsLoading);

            firstRelease.TrySetResult(Works(1));
            await first.WaitAsync(Budget);
            Assert.True(vm.IsLoading);
            Assert.Empty(vm.Items);
            Assert.Equal(0, vm.TotalCount);

            secondRelease.TrySetResult(Works(45));
            await resumed.WaitAsync(Budget);
            Assert.Equal(45, vm.TotalCount);
            Assert.Equal(Enumerable.Range(1, 20), vm.Items.Select(item => item.ID));
            Assert.Equal("1 / 3", vm.PageText);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            firstRelease.TrySetResult([]);
            secondRelease.TrySetResult([]);
            await ObserveFinishedAsync(first, resumed ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task Resume_DuringInitialLoadSharesTheActualBlockedReadAndQuery()
    {
        var blockedStarted = Signal();
        var blockedRelease = new TaskCompletionSource<HashSet<int>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedReads = 0;
        var queries = 0;
        var vm = new TagSearchResultsViewModel((_, _, _, _) =>
        {
            queries++;
            return Task.FromResult(Works(25));
        }, () =>
        {
            blockedReads++;
            blockedStarted.TrySetResult();
            return blockedRelease.Task;
        }, NullLogger.Instance);
        var initial = vm.InitializeAsync("科幻", 2023, 2026);
        Task? loaded = null;
        try
        {
            await blockedStarted.Task.WaitAsync(Budget);
            loaded = vm.ResumeAsync();
            Assert.Equal(1, blockedReads);
            Assert.False(initial.IsCompleted);
            Assert.False(loaded.IsCompleted);

            blockedRelease.TrySetResult([]);
            await Task.WhenAll(initial, loaded).WaitAsync(Budget);
            Assert.Equal(1, blockedReads);
            Assert.Equal(1, queries);
            Assert.Equal(25, vm.TotalCount);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            blockedRelease.TrySetResult([]);
            await ObserveFinishedAsync(initial, loaded ?? Task.CompletedTask);
        }
    }

    [Fact]
    public async Task SuspendResume_RepeatedInterruptionsKeepTheLatestQueryUsable()
    {
        var started = new[] { Signal(), Signal(), Signal() };
        var releases = new[] { ResultsGate(), ResultsGate(), ResultsGate() };
        var queries = 0;
        var vm = new TagSearchResultsViewModel((_, _, _, token) =>
        {
            var index = queries++;
            started[index].TrySetResult();
            return releases[index].Task.WaitAsync(token);
        }, () => Task.FromResult(new HashSet<int>()), NullLogger.Instance);
        var pending = new List<Task>();
        try
        {
            pending.Add(vm.InitializeAsync("科幻", 2023, 2026));
            await started[0].Task.WaitAsync(Budget);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                vm.Suspend();
                await pending[^1].WaitAsync(Budget);
                pending.Add(vm.ResumeAsync());
                Assert.Equal(attempt + 2, queries);
                await started[attempt + 1].Task.WaitAsync(Budget);
                Assert.True(vm.IsLoading);
            }

            releases[2].TrySetResult(Works(25));
            await pending[^1].WaitAsync(Budget);
            Assert.Equal(25, vm.TotalCount);
            Assert.Equal(Enumerable.Range(1, 20), vm.Items.Select(item => item.ID));
            Assert.False(vm.IsLoading);
            Assert.True(vm.CanGoNext);
        }
        finally
        {
            foreach (var release in releases)
                release.TrySetResult([]);
            await ObserveFinishedAsync(pending.ToArray());
        }
    }

    [Theory]
    [InlineData("http", "搜索失败：offline")]
    [InlineData("metadata", "数据源请求失败：offline")]
    [InlineData("json", "搜索结果解析失败：offline")]
    public async Task Resume_AfterSearchFailureRetriesTheSameTargetAndThenReusesCompletedResults(
        string failure,
        string expectedMessage)
    {
        var queries = new List<(string Tag, int FromYear, int ToYear)>();
        var vm = new TagSearchResultsViewModel((tag, fromYear, toYear, _) =>
        {
            queries.Add((tag, fromYear, toYear));
            return queries.Count == 1
                ? Task.FromException<List<Anime>>(Failure(failure))
                : Task.FromResult(Works(25));
        }, () => Task.FromResult(new HashSet<int>()), NullLogger.Instance);
        await vm.InitializeAsync("科幻", 2023, 2026);
        Assert.Equal(expectedMessage, vm.ResultText);
        Assert.False(vm.IsLoading);
        Assert.Empty(vm.Items);

        vm.Suspend();
        await vm.ResumeAsync();

        Assert.Equal(new[] { ("科幻", 2023, 2026), ("科幻", 2023, 2026) }, queries);
        Assert.Equal(25, vm.TotalCount);
        Assert.Equal(Enumerable.Range(1, 20), vm.Items.Select(item => item.ID));
        Assert.Equal("找到 25 部番剧 · 第 1/2 页", vm.ResultText);
        Assert.False(vm.IsLoading);
        await vm.ResumeAsync();
        vm.Suspend();
        await vm.ResumeAsync();
        Assert.Equal(2, queries.Count);
    }

    [Theory]
    [InlineData(2015, 2026, "年份跨度超过 10 年，请缩小搜索范围")]
    [InlineData(2026, 2025, "起始年份不能晚于结束年份")]
    public async Task Resume_InvalidYearRangeDoesNotAutomaticallyStartAnotherLoad(
        int fromYear,
        int toYear,
        string expectedMessage)
    {
        var fixture = new QueryFixture(25);
        var vm = fixture.CreateViewModel();
        await vm.InitializeAsync("科幻", fromYear, toYear);
        var loadingChanges = new List<bool>();
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsLoading))
                loadingChanges.Add(vm.IsLoading);
        };

        vm.Suspend();
        await vm.ResumeAsync();

        Assert.Equal(0, fixture.Queries);
        Assert.Empty(loadingChanges);
        Assert.Empty(vm.Items);
        Assert.Equal(expectedMessage, vm.ResultText);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("json")]
    public async Task ReplacedYearQuery_LateFailureCannotPublishOrMarkTheNewTargetForRetry(string failure)
    {
        var firstStarted = Signal();
        var currentStarted = Signal();
        var firstRelease = ResultsGate();
        var currentRelease = ResultsGate();
        var queries = new List<(string Tag, int FromYear, int ToYear)>();
        var vm = new TagSearchResultsViewModel((tag, fromYear, toYear, _) =>
        {
            queries.Add((tag, fromYear, toYear));
            if (queries.Count == 1)
            {
                firstStarted.TrySetResult();
                return firstRelease.Task;
            }
            currentStarted.TrySetResult();
            return currentRelease.Task;
        }, () => Task.FromResult(new HashSet<int>()), NullLogger.Instance);
        var first = vm.InitializeAsync("科幻", 2023, 2026);
        Task? current = null;
        try
        {
            await firstStarted.Task.WaitAsync(Budget);
            current = vm.ChangeYearRangeAsync(2022, 2026);
            await currentStarted.Task.WaitAsync(Budget);
            var currentText = vm.ResultText;

            firstRelease.TrySetException(Failure(failure));
            await first.WaitAsync(Budget);
            Assert.True(vm.IsLoading);
            Assert.Empty(vm.Items);
            Assert.Equal(currentText, vm.ResultText);
            Assert.False(current.IsCompleted);

            currentRelease.TrySetResult(Works(25));
            await current.WaitAsync(Budget);
            Assert.Equal(new[] { ("科幻", 2023, 2026), ("科幻", 2022, 2026) }, queries);
            Assert.Equal(25, vm.TotalCount);
            Assert.False(vm.IsLoading);
            vm.Suspend();
            await vm.ResumeAsync();
            Assert.Equal(2, queries.Count);
        }
        finally
        {
            firstRelease.TrySetResult([]);
            currentRelease.TrySetResult([]);
            await ObserveFinishedAsync(first, current ?? Task.CompletedTask);
        }
    }

    private static Exception Failure(string kind) => kind switch
    {
        "http" => new HttpRequestException("offline"),
        "metadata" => new MetadataServiceException("offline"),
        "json" => new JsonException("offline"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskCompletionSource Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<List<Anime>> ResultsGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveFinishedAsync(params Task[] tasks)
    {
        try
        {
            await Task.WhenAll(tasks).WaitAsync(Budget);
        }
        catch (OperationCanceledException)
        {
            // 生命周期取消允许任务取消结束；门控已释放，不留下活动请求。
        }
    }

    private static List<Anime> Works(int count)
        => Enumerable.Range(1, count)
            .Select(id => new Anime(id, $"合成作品{id}", null, [], null, null,
                string.Empty, 2026, 1, Score: 10 - id / 100d))
            .ToList();

    private sealed class QueryFixture(int count)
    {
        public HashSet<int> Blocked { get; set; } = [];
        public int Queries { get; private set; }
        public int BlockedReads { get; private set; }

        public TagSearchResultsViewModel CreateViewModel()
            => new((_, _, _, _) =>
            {
                Queries++;
                return Task.FromResult(Works(count));
            }, () =>
            {
                BlockedReads++;
                return Task.FromResult(Blocked.ToHashSet());
            }, NullLogger.Instance);
    }
}
