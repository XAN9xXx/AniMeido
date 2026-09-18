using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>放送日历“今日一抽”：当天固定的顺序、换一个的跳过规则，以及重启后保留结果。</summary>
public sealed class CurrentSeasonDailyPickTests : DbTestBase
{
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
        var tracking = new TrackingService(DbFactory);
        Assert.Null(await tracking.LoadCalendarDailyPickAsync());

        await tracking.SaveCalendarDailyPickAsync(new DateOnly(2026, 9, 18), 42);

        Assert.Equal((new DateOnly(2026, 9, 18), 42), await tracking.LoadCalendarDailyPickAsync());
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
        var tracking = new TrackingService(DbFactory);
        await tracking.SaveCalendarDailyPickAsync(
            DateOnly.FromDateTime(DateTime.Today).AddDays(-1),
            ScheduleSource.Ids[^1]);

        var vm = await LoadAsync();

        var expected = CurrentSeasonViewModel.BuildDailyOrder(
            ScheduleSource.Ids,
            DateOnly.FromDateTime(DateTime.Today))[0];
        Assert.Equal(expected, vm.DailyPick?.Anime.ID);
    }

    private async Task<CurrentSeasonViewModel> LoadAsync()
    {
        var vm = new CurrentSeasonViewModel(new ScheduleSource(), new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.True(vm.HasData);
        return vm;
    }

    private sealed class ScheduleSource : IAnimeDataSource
    {
        public static readonly int[] Ids = [1, 2, 3, 4, 5];

        private static readonly int Today =
            AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(Ids.Select(Item).ToList());

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(new List<Anime>());

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
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int)>>();

        private static Anime Item(int id) => new(
            id,
            $"作品{id}",
            null,
            [],
            new DateOnly(2026, 7, 3),
            null,
            string.Empty,
            2026,
            7,
            Today,
            6.0 + id * 0.1);
    }
}
