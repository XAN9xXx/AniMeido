using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>放送日历第八格“其他”：本季不在周更表里的剧场版、OVA 等。</summary>
public sealed class CurrentSeasonOtherTests : DbTestBase
{
    [Fact]
    public async Task Load_PutsSeasonOnlyAnimeIntoOtherCell()
    {
        await RunProductionMigrationAsync();
        var vm = await LoadAsync(new ScheduleSource(seasonFails: false));

        var other = vm.Days.Single(day => day.IsOther);
        Assert.Equal("1 部", other.CountText);
        Assert.Equal("2", vm.TotalCountText);

        vm.SelectDay(CalendarDay.OtherKey);

        var entry = Assert.Single(vm.VisibleEntries);
        Assert.Equal(10, entry.Anime.ID);
        Assert.True(entry.IsOther);
        Assert.False(vm.IsSortApplicable);
    }

    [Fact]
    public async Task Load_KeepsWeeklyScheduleWhenSeasonQueryFails()
    {
        // 按季查询失败只影响“其他”一格，周一到周日照常显示。
        await RunProductionMigrationAsync();
        var vm = await LoadAsync(new ScheduleSource(seasonFails: true));

        Assert.False(vm.IsError);
        Assert.Equal("1", vm.TotalCountText);
        Assert.Equal("–", vm.Days.Single(day => day.IsOther).CountText);
    }

    [Fact]
    public async Task Load_ShowsWeeklyScheduleBeforeSlowSeasonQueryReturns()
    {
        // 按季查询慢时，周一到周日先显示，“其他”一格单独显示加载中。
        await RunProductionMigrationAsync();
        var gate = new TaskCompletionSource();
        var vm = new CurrentSeasonViewModel(
            new ScheduleSource(seasonFails: false) { SeasonGate = gate },
            new TrackingService(DbFactory));

        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);

        Assert.True(vm.HasData);
        Assert.False(vm.IsLoading);
        Assert.Equal("1", vm.TotalCountText);
        Assert.Equal("…", vm.Days.Single(day => day.IsOther).CountText);

        gate.SetResult();
        await vm.PendingOthersLoad;

        Assert.Equal("2", vm.TotalCountText);
        Assert.Equal("1 部", vm.Days.Single(day => day.IsOther).CountText);
    }

    [Fact]
    public async Task LeavingPage_CancelsSeasonQueryAndReturningRestartsIt()
    {
        // 离开页面时取消；从详情页返回复用同一个页面，还没有结果的查询重新开始。
        await RunProductionMigrationAsync();
        var gate = new TaskCompletionSource();
        var source = new ScheduleSource(seasonFails: false) { SeasonGate = gate };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        var requestsBeforeLeaving = source.CurrentSeasonRequests;

        vm.CancelSupplementaryLoads();
        vm.ResumeSupplementaryLoads();
        // 再次回到页面时查询已在进行，不重复发起。
        vm.ResumeSupplementaryLoads();

        Assert.Equal(requestsBeforeLeaving + 1, source.CurrentSeasonRequests);
        gate.SetResult();
        await vm.PendingOthersLoad;
        Assert.Equal("1 部", vm.Days.Single(day => day.IsOther).CountText);
    }

    private async Task<CurrentSeasonViewModel> LoadAsync(ScheduleSource source)
    {
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        await vm.PendingOthersLoad;
        Assert.True(vm.HasData);
        return vm;
    }

    private sealed class ScheduleSource(bool seasonFails) : IAnimeDataSource
    {
        private static readonly int Today =
            AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        /// <summary>设置后，本季的按季查询等它完成才返回（番剧时光机查的往年不受影响）。</summary>
        public TaskCompletionSource? SeasonGate { get; init; }

        public int CurrentSeasonRequests { get; private set; }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(new List<Anime> { Item(1, Today, AnimeMediaFormat.Television) });

        // 按季查询同时返回周更作品和一部剧场版，只有剧场版应归入“其他”。
        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            if (year == SeasonHelper.GetCurrentSeason().year)
            {
                CurrentSeasonRequests++;
                if (SeasonGate is { } gate)
                    await gate.Task;
            }

            return seasonFails
                ? throw new HttpRequestException("offline")
                : [Item(1, Today, AnimeMediaFormat.Television), Item(10, null, AnimeMediaFormat.Movie)];
        }

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

        private static Anime Item(int id, int? weekday, AnimeMediaFormat format) => new(
            id,
            $"作品{id}",
            null,
            [],
            new DateOnly(2026, 8, 7),
            null,
            string.Empty,
            2026,
            7,
            weekday,
            7.0,
            MediaFormat: format);
    }
}
