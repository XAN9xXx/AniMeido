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

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            LastRequest = new PastSeasonTarget(year, season);
            RequestCount++;
            return Fails
                ? Task.FromException<List<Anime>>(new HttpRequestException("offline"))
                : Task.FromResult(new List<Anime> { Item(101, 8.6), Item(102, 7.9), Item(103, null) });
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
