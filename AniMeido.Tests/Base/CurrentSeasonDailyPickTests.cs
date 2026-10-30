using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>放送日历“今日一抽”：当天固定的顺序、换一个的跳过规则，以及重启后保留结果。</summary>
[Collection(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonDailyPickTests : DbTestBase, IAsyncLifetime
{
    private readonly PersonalFeatureStateStore _featureState;
    private readonly List<CurrentSeasonViewModel> _viewModels = [];
    private readonly List<Task> _previousLoads = [];

    private static readonly DateOnly TestDay = new(2026, 9, 18);
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    public CurrentSeasonDailyPickTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
        CurrentSeasonViewModel.ResetTimeMachineSession();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var vm in _viewModels)
        {
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
        }
        try
        {
            await Task.WhenAll(_previousLoads.Concat(_viewModels.SelectMany(vm => new[]
            {
                vm.LoadSeasonalAnimeCommand.ExecutionTask ?? Task.CompletedTask,
                vm.NextDailyPickCommand.ExecutionTask ?? Task.CompletedTask,
                vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask,
                vm.PendingOthersLoad,
                vm.PendingTimeMachineLoad,
            }))).WaitAsync(Budget);
        }
        finally
        {
            // 加载结束后才会登记最终保存；数据库由随后执行的 DbTestBase.Dispose 清理。
            await Task.WhenAll(_viewModels.Select(vm => vm.PendingTimeMachineShownSave)).WaitAsync(Budget);
        }
    }

    [Fact]
    public void BuildDailyOrder_IsStableForTheSameDayAndCoversAllIds()
    {
        var day = new DateOnly(2026, 9, 18);
        int[] ids = [5, 1, 9, 3, 7, 2];

        var first = CurrentSeasonViewModel.BuildDailyOrder(ids, day);
        var again = CurrentSeasonViewModel.BuildDailyOrder(ids.Reverse(), day);

        Assert.Equal(first, again);
        Assert.Equal(ids.Order(), first.Order());
    }

    [Fact]
    public void FindNextDailyPick_SkipsIneligibleAndWrapsAround()
    {
        int[] order = [4, 8, 15, 16, 23];

        // 从 15 之后找：16 不符合，23 符合。
        Assert.Equal(23, CurrentSeasonViewModel.FindNextDailyPick(order, 15, id => id != 16));
        // 到末尾回到开头；当前这部本身不算。
        Assert.Equal(4, CurrentSeasonViewModel.FindNextDailyPick(order, 23, _ => true));
        // 当前不在顺序里时从头开始。
        Assert.Equal(4, CurrentSeasonViewModel.FindNextDailyPick(order, 99, _ => true));
        // 没有其他符合条件的作品。
        Assert.Null(CurrentSeasonViewModel.FindNextDailyPick(order, 8, id => id == 8));
    }

    [Fact]
    public void PickDailyTags_KeepsCatalogTagsInOrder()
    {
        // 去掉季度、制作公司等杂项，按返回顺序去重，最多三个。
        var tags = CurrentSeasonViewModel.PickDailyTags(
            ["2023年10月", " 奇幻 ", "MADHouse", null, "", "奇幻", "漫画改", "治愈", "冒险"]);

        Assert.Equal(new[] { "奇幻", "漫画改", "治愈" }, tags);
        Assert.Empty(CurrentSeasonViewModel.PickDailyTags(["TV", "2024"]));
    }

    [Fact]
    public async Task CalendarDailyPick_RoundTripsThroughConfig()
    {
        await RunProductionMigrationAsync();
        Assert.Null(await _featureState.LoadCalendarDailyPickAsync());

        await _featureState.SaveCalendarDailyPickAsync(new DateOnly(2026, 9, 18), 42);

        Assert.Equal((new DateOnly(2026, 9, 18), 42), await _featureState.LoadCalendarDailyPickAsync());
    }

    [Fact]
    public async Task DailyPick_IsKeptAfterRestartOnTheSameDay()
    {
        await RunProductionMigrationAsync();
        var first = await LoadAsync();
        Assert.NotNull(first.DailyPick);

        await first.NextDailyPickCommand.ExecuteAsync(null);
        var changed = first.DailyPick!.Anime.ID;

        // 模拟重启：新建 ViewModel 从同一个数据库读取。
        var restarted = await LoadAsync();

        Assert.Equal(changed, restarted.DailyPick?.Anime.ID);
    }

    [Fact]
    public async Task DailyPick_StartsOverWhenStoredDayIsNotToday()
    {
        await RunProductionMigrationAsync();
        await _featureState.SaveCalendarDailyPickAsync(
            TestDay.AddDays(-1),
            ScheduleSource.Ids[^1]);

        var vm = await LoadAsync();

        var expected = CurrentSeasonViewModel.BuildDailyOrder(
            ScheduleSource.Ids,
            TestDay)[0];
        Assert.Equal(expected, vm.DailyPick?.Anime.ID);
    }

    [Theory]
    [InlineData(2026, 3, 31)]
    [InlineData(2026, 12, 31)]
    public async Task DailyPickUsesTheNewLocalDateAcrossDaySeasonAndYearBoundaries(int year, int month, int day)
    {
        await RunProductionMigrationAsync();
        var clock = new CalendarTestTime(new DateOnly(year, month, day));
        var source = new ScheduleSource(clock.Today);
        var tracking = new TrackingService(DbFactory);
        var vm = CreateViewModel(source, tracking, clock);
        var weekday = AnimeListPresentation.ToBangumiWeekday(clock.Today.DayOfWeek);
        Assert.Equal(weekday, vm.SelectedWeekday);
        Assert.Equal(weekday, Assert.Single(vm.Days, item => item.IsToday).Weekday);

        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.True(vm.HasData);
        Assert.Equal(clock.Today, (await _featureState.LoadCalendarDailyPickAsync())!.Value.Date);
        Assert.Contains((year, SeasonHelper.FromMonth(month)), source.SeasonRequests);

        _previousLoads.AddRange([vm.PendingOthersLoad, vm.PendingTimeMachineLoad, vm.PendingTimeMachineShownSave]);
        clock.Today = clock.Today.AddDays(1);
        await vm.ReloadStatusesAsync();

        Assert.Equal(clock.Today, (await _featureState.LoadCalendarDailyPickAsync())!.Value.Date);
        Assert.Equal(CurrentSeasonViewModel.BuildDailyOrder(ScheduleSource.Ids, clock.Today)[0], vm.DailyPick?.Anime.ID);
        Assert.Equal(clock.Today.Year - vm.TimeMachineYearsAgo, vm.TimeMachineTarget.Year);
        Assert.Equal(SeasonHelper.FromMonth(clock.Today.Month), vm.TimeMachineTarget.Season);
    }

    private async Task<CurrentSeasonViewModel> LoadAsync()
    {
        var clock = new CalendarTestTime(TestDay);
        var vm = CreateViewModel(new ScheduleSource(TestDay), new TrackingService(DbFactory), clock);
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.True(vm.HasData);
        return vm;
    }

    private CurrentSeasonViewModel CreateViewModel(
        ScheduleSource source, TrackingService tracking, CalendarTestTime clock)
    {
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, clock, NullLogger.Instance);
        _viewModels.Add(vm);
        return vm;
    }

    private sealed class ScheduleSource(DateOnly date) : IAnimeDataSource
    {
        public static readonly int[] Ids = [1, 2, 3, 4, 5];

        private readonly int _weekday = AnimeListPresentation.ToBangumiWeekday(date.DayOfWeek);
        internal List<(int Year, Season Season)> SeasonRequests { get; } = [];

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(Ids.Select(Item).ToList());

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            SeasonRequests.Add((year, season));
            return Task.FromResult(new List<Anime>());
        }

        // 简介和标签只是补充信息，返回空即可。
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
            => Task.FromResult<Anime?>(null);

        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Tag>());

        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int, int)>>();

        private Anime Item(int id) => new(
            id,
            $"作品{id}",
            null,
            [],
            new DateOnly(2026, 7, 3),
            null,
            string.Empty,
            2026,
            7,
            _weekday,
            6.0 + id * 0.1,
            MediaFormat: AnimeMediaFormat.Television);
    }
}
