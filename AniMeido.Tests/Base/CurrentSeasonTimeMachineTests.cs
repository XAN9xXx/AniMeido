using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using System.Reflection;

namespace AniMeido.Tests;

/// <summary>放送日历下方的番剧时光机：往年同一季、年份切换与失败时的状态。</summary>
[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonTimeMachineTests : DbTestBase, IAsyncLifetime
{
    private readonly PersonalFeatureStateStore _featureState;

    private static readonly DateOnly TestDay = new(2026, 9, 18);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private readonly List<CurrentSeasonViewModel> _viewModels = [];
    private readonly List<Task> _startedTasks = [];

    // 时光机的年份与抽到的那一批在应用运行期间保留，每个测试从干净状态开始。
    public CurrentSeasonTimeMachineTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var vm in _viewModels)
        {
            vm.CancelSupplementaryLoads();
        }
        try
        {
            await Task.WhenAll(_startedTasks.Concat(_viewModels.SelectMany(vm => new[]
            {
                vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask,
                vm.PendingOthersLoad,
                vm.PendingTimeMachineLoad,
            }))).WaitAsync(Budget);
        }
        finally
        {
            // 加载可能最后才登记保存；xUnit 会在此结束后调用继承的 DbTestBase.Dispose。
            await Task.WhenAll(_viewModels.Select(vm => vm.PendingTimeMachineShownSave)).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task TimeMachine_LoadsSameSeasonTenYearsAgoWithMarks()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(101, AnimeTrackingStatus.Completed);
        var source = new SeasonSource();
        var vm = CreateViewModel(source, tracking);
        vm.SetDiscoverCapacity(10);

        await LoadTimeMachineAsync(vm).WaitAsync(Budget);

        var (year, season) = (TestDay.Year, SeasonHelper.FromMonth(TestDay.Month));
        Assert.Equal(new PastSeasonTarget(year - 10, season), source.LastRequest);
        Assert.Equal(new PastSeasonTarget(year - 10, season), vm.TimeMachineTarget);
        // 顺序随机；容量足够时有评分的两部都在。
        Assert.Equal(new[] { 101, 102 }, vm.TimeMachinePicks.Select(entry => entry.Anime.ID).Order());
        Assert.Equal(
            AnimeTrackingStatus.Completed,
            vm.TimeMachinePicks.Single(entry => entry.Anime.ID == 101).Status);
        Assert.False(vm.IsTimeMachineLoading);
    }

    [Fact]
    public async Task TimeMachine_CapacityLimitsVisibleRows()
    {
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(1);

        await LoadTimeMachineAsync(vm).WaitAsync(Budget);

        Assert.Single(vm.TimeMachinePicks);
    }

    [Fact]
    public async Task TimeMachine_SwitchingYearsRequestsThatSeason()
    {
        await RunProductionMigrationAsync();
        var source = new SeasonSource();
        var vm = CreateViewModel(source, new TrackingService(DbFactory));

        SelectYearsAgo(vm, 5);
        await vm.PendingTimeMachineLoad.WaitAsync(Budget);

        var (year, season) = (TestDay.Year, SeasonHelper.FromMonth(TestDay.Month));
        Assert.Equal(new PastSeasonTarget(year - 5, season), source.LastRequest);
        Assert.StartsWith($"{year - 5} 年", vm.TimeMachineCaption);
    }

    [Fact]
    public async Task TimeMachine_ReopeningPageKeepsDrawAndYear()
    {
        // 切换页面再回来会新建页面，但沿用本次运行中抽到的那一批和选中的年份。
        await RunProductionMigrationAsync();
        var first = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        first.SetDiscoverCapacity(1);
        await LoadTimeMachineAsync(first).WaitAsync(Budget);
        var shown = first.TimeMachinePicks.Single().Anime.ID;

        var reopened = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        reopened.SetDiscoverCapacity(1);
        await LoadTimeMachineAsync(reopened).WaitAsync(Budget);

        Assert.Equal(shown, reopened.TimeMachinePicks.Single().Anime.ID);
        Assert.Equal(10, reopened.TimeMachineYearsAgo);
    }

    [Fact]
    public async Task TimeMachine_SwitchingYearsKeepsEachYearsDraw()
    {
        // 切换年份不重新抽：切回来还是第一次看到这个年份时抽到的那一批。
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(1);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);
        var first = vm.TimeMachinePicks.Single().Anime.ID;

        SelectYearsAgo(vm, 5);
        await vm.PendingTimeMachineLoad.WaitAsync(Budget);
        SelectYearsAgo(vm, 10);
        await vm.PendingTimeMachineLoad.WaitAsync(Budget);

        Assert.Equal(first, vm.TimeMachinePicks.Single().Anime.ID);
    }

    [Fact]
    public void KeepStoredOrder_DropsMissingAndAppendsNew()
    {
        var pool = new[] { 1, 2, 3, 4 }
            .Select(id => new TimeMachineEntry(id, Item(id, 7.0)))
            .ToList();

        var ordered = CurrentSeasonViewModel.KeepStoredOrder(pool, [3, 9, 1], new Random(7));

        Assert.Equal(new[] { 3, 1 }, ordered.Take(2).Select(entry => entry.Anime.ID));
        Assert.Equal(new[] { 2, 4 }, ordered.Skip(2).Select(entry => entry.Anime.ID).Order());
    }

    [Fact]
    public async Task TimeMachine_FailureShowsRetryInsteadOfError()
    {
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(
            new SeasonSource { Fails = true },
            new TrackingService(DbFactory));

        await LoadTimeMachineAsync(vm).WaitAsync(Budget);

        Assert.True(vm.IsTimeMachineFailed);
        Assert.False(vm.IsError);
        Assert.True(vm.HasTimeMachineStatusText);
    }

    [Fact]
    public async Task TimeMachine_TogglePlanToWatchWritesAndCancels()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = CreateViewModel(new SeasonSource(), tracking);
        vm.SetDiscoverCapacity(10);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);

        await vm.ToggleTimeMachineStatusAsync(102, AnimeTrackingStatus.PlanToWatch);
        Assert.Equal("补番中 ✓", vm.TimeMachinePicks.Single(entry => entry.Anime.ID == 102).PlanLabel);
        Assert.Contains(
            await tracking.GetAllTrackingAsync(),
            row => row.AnimeId == 102 && row.Status == AnimeTrackingStatus.PlanToWatch);

        await vm.ToggleTimeMachineStatusAsync(102, AnimeTrackingStatus.PlanToWatch);
        Assert.Equal(AnimeTrackingStatus.None, vm.TimeMachinePicks.Single(entry => entry.Anime.ID == 102).Status);
    }

    [Fact]
    public async Task TimeMachine_ReadDoesNotOverwriteWriteThatStartedBeforeIt()
    {
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);
        var entry = vm.TimeMachinePicks.Single(item => item.Anime.ID == 102);

        entry.IsSavingStatus = true;
        var snapshot = vm.BeginStatusRead();
        entry.Status = AnimeTrackingStatus.PlanToWatch;
        entry.IsSavingStatus = false;
        vm.ApplyStatuses(new Dictionary<int, AnimeTrackingStatus>(), snapshot);

        Assert.Equal(AnimeTrackingStatus.PlanToWatch, entry.Status);
    }

    [Fact]
    public async Task TimeMachine_ReadDoesNotOverwriteWriteThatStartedDuringIt()
    {
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);

        var snapshot = vm.BeginStatusRead();
        await vm.ToggleTimeMachineStatusAsync(102, AnimeTrackingStatus.PlanToWatch);
        vm.ApplyStatuses(new Dictionary<int, AnimeTrackingStatus>(), snapshot);

        Assert.Equal(
            AnimeTrackingStatus.PlanToWatch,
            vm.TimeMachinePicks.Single(item => item.Anime.ID == 102).Status);
    }

    [Fact]
    public async Task TimeMachine_ReadDoesNotRemoveSavingItemForStaleBlockedRead()
    {
        await RunProductionMigrationAsync();
        var vm = CreateViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);
        var entry = vm.TimeMachinePicks.Single(item => item.Anime.ID == 102);

        entry.IsSavingStatus = true;
        var snapshot = vm.BeginStatusRead();
        entry.Status = AnimeTrackingStatus.PlanToWatch;
        entry.IsSavingStatus = false;
        vm.ApplyStatuses(
            new Dictionary<int, AnimeTrackingStatus> { [102] = AnimeTrackingStatus.Blocked },
            snapshot);

        Assert.Contains(vm.TimeMachinePicks, item => ReferenceEquals(item, entry));
        Assert.Equal(AnimeTrackingStatus.PlanToWatch, entry.Status);
    }

    [Fact]
    public async Task TimeMachine_LeavingPageCancelsAndReturningRestarts()
    {
        // 离开页面时取消，不停在“加载中”；回到同一个页面时重新加载。
        await RunProductionMigrationAsync();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new SeasonSource { Gate = gate };
        var vm = CreateViewModel(source, new TrackingService(DbFactory));
        Task cancelled = Task.CompletedTask;
        try
        {
            vm.SetDiscoverCapacity(10);
            cancelled = LoadTimeMachineAsync(vm);

            vm.CancelSupplementaryLoads();
            Assert.False(vm.IsTimeMachineLoading);

            vm.ResumeSupplementaryLoads();
            vm.ResumeSupplementaryLoads();
            Assert.Equal(2, source.RequestCount);

            gate.TrySetResult();
            await cancelled.WaitAsync(TimeSpan.FromSeconds(10));
            await vm.PendingTimeMachineLoad.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, vm.TimeMachinePicks.Count);
            Assert.False(vm.IsTimeMachineLoading);
        }
        finally
        {
            gate.TrySetResult();
            vm.CancelSupplementaryLoads();
            try
            {
                await Task.WhenAll(cancelled, vm.PendingOthersLoad, vm.PendingTimeMachineLoad)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await vm.PendingTimeMachineShownSave.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [Fact]
    public void OrderForDisplay_PutsUnseenFirstAndStartsOverWhenAllSeen()
    {
        var pool = new[] { 1, 2, 3, 4 }
            .Select(id => new TimeMachineEntry(id, Item(id, 7.0)))
            .ToList();

        var ordered = CurrentSeasonViewModel.OrderForDisplay(pool, new HashSet<int> { 1, 2 }, new Random(7), out var startOver);
        Assert.False(startOver);
        Assert.Equal(new[] { 3, 4 }, ordered.Take(2).Select(entry => entry.Anime.ID).Order());
        Assert.Equal(new[] { 1, 2 }, ordered.Skip(2).Select(entry => entry.Anime.ID).Order());

        var again = CurrentSeasonViewModel.OrderForDisplay(pool, new HashSet<int> { 1, 2, 3, 4 }, new Random(7), out startOver);
        Assert.True(startOver);
        Assert.Equal(new[] { 1, 2, 3, 4 }, again.Select(entry => entry.Anime.ID).Order());
    }

    [Fact]
    public async Task TimeMachine_RestartPrefersWorksNotShownBefore()
    {
        // 候选只有 101、102 两部有评分：第二次启动换一部，第三次启动都显示过了，从头再来。
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var first = await LoadOneAsync(tracking);

        CurrentSeasonViewModel.ResetTimeMachineSession();
        var second = await LoadOneAsync(tracking);
        Assert.NotEqual(first, second);

        CurrentSeasonViewModel.ResetTimeMachineSession();
        var third = await LoadOneAsync(tracking);
        var (year, season) = (TestDay.Year, SeasonHelper.FromMonth(TestDay.Month));
        var saved = await _featureState.LoadTimeMachineShownAsync();
        Assert.Equal(new[] { third }, saved[(year - 10, season)]);
    }

    [Fact]
    public async Task TimeMachine_ShownRecordKeepsOnlyCurrentSeasons()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveTimeMachineShownAsync(new Dictionary<(int Year, Season Season), IReadOnlyList<int>>
        {
            [(2000, Season.Winter)] = [1, 2],
        });

        var shown = await LoadOneAsync(tracking);

        var (year, season) = (TestDay.Year, SeasonHelper.FromMonth(TestDay.Month));
        var saved = await _featureState.LoadTimeMachineShownAsync();
        Assert.DoesNotContain((2000, Season.Winter), saved.Keys);
        Assert.Equal(new[] { shown }, saved[(year - 10, season)]);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task TimeMachine_ResetRegistersTheActualSaveTaskBeforeAwaitingIt()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var target = (Year: TestDay.Year - 10, Season: SeasonHelper.FromMonth(TestDay.Month));
        await _featureState.SaveTimeMachineShownAsync(
            new Dictionary<(int Year, Season Season), IReadOnlyList<int>>
            {
                [target] = [101, 102],
            });
        var vm = CreateViewModel(new SeasonSource(), tracking);
        vm.SetDiscoverCapacity(1);
        var gateField = typeof(CurrentSeasonViewModel).GetField(
            "TimeMachineShownGate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var shownGate = Assert.IsType<SemaphoreSlim>(gateField!.GetValue(null));
        Assert.True(await shownGate.WaitAsync(TimeSpan.FromSeconds(10)));
        var beforeReset = vm.PendingTimeMachineShownSave;
        Task? loading = null;
        Task? resetSave = null;

        try
        {
            loading = LoadTimeMachineAsync(vm);
            var registered = await Task.Run(() => SpinWait.SpinUntil(
                () => !ReferenceEquals(beforeReset, vm.PendingTimeMachineShownSave),
                TimeSpan.FromSeconds(10)));
            Assert.True(registered, "reset 的实际保存任务尚未登记；这不是旧 UI 覆盖的行为断言。");
            resetSave = vm.PendingTimeMachineShownSave;

            Assert.NotSame(beforeReset, resetSave);
            Assert.False(resetSave.IsCompleted);
            Assert.False(loading.IsCompleted);
        }
        finally
        {
            shownGate.Release();
            if (loading is not null)
                await loading.WaitAsync(TimeSpan.FromSeconds(10));
            if (resetSave is not null)
                await resetSave.WaitAsync(TimeSpan.FromSeconds(10));
            await vm.PendingTimeMachineShownSave.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var shown = await _featureState.LoadTimeMachineShownAsync();
        var saved = Assert.Single(shown[target]);
        Assert.Contains(saved, new[] { 101, 102 });
        Assert.Equal(saved, Assert.Single(vm.TimeMachinePicks).Anime.ID);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task TimeMachine_OldResetCannotRepublishNewYearsPicksOrRecordThemForOldYear()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var season = SeasonHelper.FromMonth(TestDay.Month);
        var oldTarget = (Year: TestDay.Year - 10, Season: season);
        var newTarget = (Year: TestDay.Year - 5, Season: season);
        await _featureState.SaveTimeMachineShownAsync(
            new Dictionary<(int Year, Season Season), IReadOnlyList<int>>
            {
                [oldTarget] = [101, 102],
            });
        var source = new SeasonSource
        {
            ResultsForYear = year => year == oldTarget.Year
                ? [Item(101, 8.6), Item(102, 7.9)]
                : [Item(201, 8.4), Item(202, 7.7)],
        };
        var vm = CreateViewModel(source, tracking);
        vm.SetDiscoverCapacity(1);
        var gateField = typeof(CurrentSeasonViewModel).GetField(
            "TimeMachineShownGate", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(gateField);
        var shownGate = Assert.IsType<SemaphoreSlim>(gateField!.GetValue(null));
        Assert.True(await shownGate.WaitAsync(TimeSpan.FromSeconds(10)));
        var beforeReset = vm.PendingTimeMachineShownSave;
        Task? oldLoad = null;
        Task? resetSave = null;
        Task? newLoad = null;
        Task? newSave = null;
        object? newPicks = null;
        int[] newIds = [];

        try
        {
            oldLoad = LoadTimeMachineAsync(vm);
            var registered = await Task.Run(() => SpinWait.SpinUntil(
                () => !ReferenceEquals(beforeReset, vm.PendingTimeMachineShownSave),
                TimeSpan.FromSeconds(10)));
            Assert.True(registered, "旧年份已到达重置保存的等待点。");
            resetSave = vm.PendingTimeMachineShownSave;
            Assert.False(resetSave.IsCompleted);
            Assert.False(oldLoad.IsCompleted);

            SelectYearsAgo(vm, 5);
            newLoad = vm.PendingTimeMachineLoad;
            await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
            newPicks = vm.TimeMachinePicks;
            newIds = vm.TimeMachinePicks.Select(entry => entry.Anime.ID).ToArray();
            newSave = vm.PendingTimeMachineShownSave;
            Assert.Single(newIds);
            Assert.Contains(newIds[0], new[] { 201, 202 });
            Assert.NotSame(resetSave, newSave);
            Assert.False(newSave.IsCompleted);
        }
        finally
        {
            shownGate.Release();
            if (oldLoad is not null)
                await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
            if (resetSave is not null)
                await resetSave.WaitAsync(TimeSpan.FromSeconds(10));
            if (newLoad is not null)
                await newLoad.WaitAsync(TimeSpan.FromSeconds(10));
            if (newSave is not null)
                await newSave.WaitAsync(TimeSpan.FromSeconds(10));
            await vm.PendingTimeMachineShownSave.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // 即使旧任务碰巧读到共享的新名单，也不能再次发布集合或按旧年份记下新作品。
        Assert.Same(newPicks, vm.TimeMachinePicks);
        Assert.Equal(newIds, vm.TimeMachinePicks.Select(entry => entry.Anime.ID));
        Assert.Equal(new PastSeasonTarget(newTarget.Year, newTarget.Season), vm.TimeMachineTarget);
        var shown = await _featureState.LoadTimeMachineShownAsync();
        Assert.Equal(newIds, shown[newTarget]);
        Assert.All(shown.GetValueOrDefault(oldTarget, []), id => Assert.DoesNotContain(id, newIds));
    }

    private CurrentSeasonViewModel CreateViewModel(IAnimeDataSource source, TrackingService tracking)
    {
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(TestDay), NullLogger.Instance);
        _viewModels.Add(vm);
        return vm;
    }

    private Task LoadTimeMachineAsync(CurrentSeasonViewModel vm)
    {
        _startedTasks.Add(vm.PendingTimeMachineShownSave);
        var loading = vm.LoadTimeMachineAsync();
        _startedTasks.Add(loading);
        return loading;
    }

    private void SelectYearsAgo(CurrentSeasonViewModel vm, int yearsAgo)
    {
        _startedTasks.Add(vm.PendingTimeMachineShownSave);
        vm.TimeMachineYearsAgo = yearsAgo;
        _startedTasks.Add(vm.PendingTimeMachineLoad);
    }

    /// <summary>容量 1、十年前：加载并等“显示过”写完，返回显示的那一部。</summary>
    private async Task<int> LoadOneAsync(TrackingService tracking)
    {
        var vm = CreateViewModel(new SeasonSource(), tracking);
        vm.SetDiscoverCapacity(1);
        await LoadTimeMachineAsync(vm).WaitAsync(Budget);
        await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
        return vm.TimeMachinePicks.Single().Anime.ID;
    }

    private static Anime Item(int id, double? score) => new(
        id,
        $"老番{id}",
        null,
        [],
        new DateOnly(2016, 7, 1),
        null,
        string.Empty,
        2016,
        7,
        Score: score,
        MediaFormat: AnimeMediaFormat.Television);

    private sealed class SeasonSource : IAnimeDataSource
    {
        public bool Fails { get; init; }

        public Func<int, List<Anime>>? ResultsForYear { get; init; }

        public PastSeasonTarget? LastRequest { get; private set; }

        public int RequestCount { get; private set; }

        /// <summary>设置后，查询等它完成才返回。</summary>
        public TaskCompletionSource? Gate { get; init; }

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            LastRequest = new PastSeasonTarget(year, season);
            RequestCount++;
            if (Gate is { } gate)
                await gate.Task;

            return Fails
                ? throw new HttpRequestException("offline")
                : ResultsForYear?.Invoke(year) ?? [Item(101, 8.6), Item(102, 7.9), Item(103, null)];
        }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Unexpected<Task<List<Anime>>>();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Tag>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int, int)>>();
    }
}
