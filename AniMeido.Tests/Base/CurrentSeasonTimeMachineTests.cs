using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>放送日历下方的番剧时光机：往年同一季、年份切换与失败时的状态。</summary>
public sealed class CurrentSeasonTimeMachineTests : DbTestBase
{
    // 时光机的年份与抽到的那一批在应用运行期间保留，每个测试从干净状态开始。
    public CurrentSeasonTimeMachineTests() => CurrentSeasonViewModel.ResetTimeMachineSession();

    [Fact]
    public async Task TimeMachine_LoadsSameSeasonTenYearsAgoWithMarks()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(101, AnimeTrackingStatus.Completed);
        var source = new SeasonSource();
        var vm = new CurrentSeasonViewModel(source, tracking);
        vm.SetDiscoverCapacity(10);

        await vm.LoadTimeMachineAsync();

        var (year, season) = SeasonHelper.GetCurrentSeason();
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
        var vm = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(1);

        await vm.LoadTimeMachineAsync();

        Assert.Single(vm.TimeMachinePicks);
    }

    [Fact]
    public async Task TimeMachine_SwitchingYearsRequestsThatSeason()
    {
        await RunProductionMigrationAsync();
        var source = new SeasonSource();
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));

        vm.TimeMachineYearsAgo = 5;
        await vm.PendingTimeMachineLoad;

        var (year, season) = SeasonHelper.GetCurrentSeason();
        Assert.Equal(new PastSeasonTarget(year - 5, season), source.LastRequest);
        Assert.StartsWith($"{year - 5} 年", vm.TimeMachineCaption);
    }

    [Fact]
    public async Task TimeMachine_ReopeningPageKeepsDrawAndYear()
    {
        // 切换页面再回来会新建页面，但沿用本次运行中抽到的那一批和选中的年份。
        await RunProductionMigrationAsync();
        var first = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        first.SetDiscoverCapacity(1);
        await first.LoadTimeMachineAsync();
        var shown = first.TimeMachinePicks.Single().Anime.ID;

        var reopened = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        reopened.SetDiscoverCapacity(1);
        await reopened.LoadTimeMachineAsync();

        Assert.Equal(shown, reopened.TimeMachinePicks.Single().Anime.ID);
        Assert.Equal(10, reopened.TimeMachineYearsAgo);
    }

    [Fact]
    public async Task TimeMachine_SwitchingYearsKeepsEachYearsDraw()
    {
        // 切换年份不重新抽：切回来还是第一次看到这个年份时抽到的那一批。
        await RunProductionMigrationAsync();
        var vm = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(1);
        await vm.LoadTimeMachineAsync();
        var first = vm.TimeMachinePicks.Single().Anime.ID;

        vm.TimeMachineYearsAgo = 5;
        await vm.PendingTimeMachineLoad;
        vm.TimeMachineYearsAgo = 10;
        await vm.PendingTimeMachineLoad;

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
        var vm = new CurrentSeasonViewModel(
            new SeasonSource { Fails = true },
            new TrackingService(DbFactory));

        await vm.LoadTimeMachineAsync();

        Assert.True(vm.IsTimeMachineFailed);
        Assert.False(vm.IsError);
        Assert.True(vm.HasTimeMachineStatusText);
    }

    [Fact]
    public async Task TimeMachine_TogglePlanToWatchWritesAndCancels()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = new CurrentSeasonViewModel(new SeasonSource(), tracking);
        vm.SetDiscoverCapacity(10);
        await vm.LoadTimeMachineAsync();

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
        var vm = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await vm.LoadTimeMachineAsync();
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
        var vm = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await vm.LoadTimeMachineAsync();

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
        var vm = new CurrentSeasonViewModel(new SeasonSource(), new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        await vm.LoadTimeMachineAsync();
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
        var gate = new TaskCompletionSource();
        var source = new SeasonSource { Gate = gate };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        vm.SetDiscoverCapacity(10);
        var cancelled = vm.LoadTimeMachineAsync();

        vm.CancelSupplementaryLoads();
        Assert.False(vm.IsTimeMachineLoading);

        vm.ResumeSupplementaryLoads();
        vm.ResumeSupplementaryLoads();
        Assert.Equal(2, source.RequestCount);

        gate.SetResult();
        await cancelled;
        await vm.PendingTimeMachineLoad;
        Assert.Equal(2, vm.TimeMachinePicks.Count);
        Assert.False(vm.IsTimeMachineLoading);
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
        var (year, season) = SeasonHelper.GetCurrentSeason();
        var saved = await tracking.LoadTimeMachineShownAsync();
        Assert.Equal(new[] { third }, saved[new PastSeasonTarget(year - 10, season)]);
    }

    [Fact]
    public async Task TimeMachine_ShownRecordKeepsOnlyCurrentSeasons()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SaveTimeMachineShownAsync(new Dictionary<PastSeasonTarget, IReadOnlyList<int>>
        {
            [new PastSeasonTarget(2000, Season.Winter)] = [1, 2],
        });

        var shown = await LoadOneAsync(tracking);

        var (year, season) = SeasonHelper.GetCurrentSeason();
        var saved = await tracking.LoadTimeMachineShownAsync();
        Assert.DoesNotContain(new PastSeasonTarget(2000, Season.Winter), saved.Keys);
        Assert.Equal(new[] { shown }, saved[new PastSeasonTarget(year - 10, season)]);
    }

    /// <summary>容量 1、十年前：加载并等“显示过”写完，返回显示的那一部。</summary>
    private static async Task<int> LoadOneAsync(TrackingService tracking)
    {
        var vm = new CurrentSeasonViewModel(new SeasonSource(), tracking);
        vm.SetDiscoverCapacity(1);
        await vm.LoadTimeMachineAsync();
        await vm.PendingTimeMachineShownSave;
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
                : [Item(101, 8.6), Item(102, 7.9), Item(103, null)];
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
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();
    }
}
