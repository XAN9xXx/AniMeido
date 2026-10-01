using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>放送日历只把已确认且有排期的 TV 放入星期格，其余归入“其他”。</summary>
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
    public async Task OtherPriority_LoadAndToggleReorderMarkedItemsImmediately()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(11, AnimeTrackingStatus.Following);
        var source = new ScheduleSource(seasonFails: false)
        {
            Catalog = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Television),
                ScheduleSource.Item(10, null, AnimeMediaFormat.Movie) with { AirDate = new DateOnly(2026, 7, 1) },
                ScheduleSource.Item(11, null, AnimeMediaFormat.Movie) with { AirDate = new DateOnly(2026, 9, 1) }],
        };
        var vm = await LoadAsync(source);
        vm.SelectDay(CalendarDay.OtherKey);
        var marked = vm.VisibleEntries.Single(entry => entry.Anime.ID == 11);
        Assert.Equal(new[] { 11, 10 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        Assert.Equal("你标记的 1 部排在前面 · 按上映日期排列", vm.ListCaption);

        await vm.ToggleStatusAsync(11, AnimeTrackingStatus.Following);

        Assert.Equal(new[] { 10, 11 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        Assert.Equal("网络动画、剧场版、OVA 等 · 按上映日期排列", vm.ListCaption);
        await vm.ToggleStatusAsync(11, AnimeTrackingStatus.Watching);
        Assert.Equal(new[] { 11, 10 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        Assert.Same(marked, vm.VisibleEntries[0]);
        Assert.False(marked.IsSavingStatus);
    }

    [Fact]
    public async Task OtherPriority_StatusReloadReordersAndKeepsUnchangedCollection()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource(seasonFails: false)
        {
            Catalog = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Television),
                ScheduleSource.Item(10, null, AnimeMediaFormat.Movie) with { AirDate = new DateOnly(2026, 7, 1) },
                ScheduleSource.Item(11, null, AnimeMediaFormat.Movie) with { AirDate = new DateOnly(2026, 9, 1) }],
        };
        var vm = await LoadAsync(source);
        vm.SelectDay(CalendarDay.OtherKey);
        Assert.Equal(new[] { 10, 11 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(11, AnimeTrackingStatus.Watching);

        await vm.ReloadStatusesAsync();

        Assert.Equal(new[] { 11, 10 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        Assert.Equal("你标记的 1 部排在前面 · 按上映日期排列", vm.ListCaption);
        var ordered = vm.VisibleEntries;
        await vm.ReloadStatusesAsync();
        Assert.Same(ordered, vm.VisibleEntries);
        await tracking.RemoveStatusAsync(11);
        await vm.ReloadStatusesAsync();
        Assert.Equal(new[] { 10, 11 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
    }

    [Fact]
    public async Task Load_KeepsWeeklyScheduleWhenSeasonQueryFails()
    {
        // 按季查询失败时，已确认的 TV 仍在星期格，未知形态不能猜成 TV。
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

    [Fact]
    public async Task Load_EmptySchedule_ShowsUnavailableOtherWithoutCurrentSeasonQuery()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource(seasonFails: false) { IsScheduleEmpty = true };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));

        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        await vm.PendingOthersLoad;
        await vm.PendingTimeMachineLoad;

        Assert.False(vm.IsLoading);
        Assert.False(vm.IsError);
        Assert.False(vm.HasData);
        Assert.Equal("–", vm.Days.Single(day => day.IsOther).CountText);
        Assert.Equal(0, source.CurrentSeasonRequests);

        vm.SelectDay(CalendarDay.OtherKey);

        Assert.Empty(vm.VisibleEntries);
        Assert.Equal("本季放送表还没有数据，暂时无法整理其他作品", vm.EmptyText);
        Assert.Equal("网络动画、剧场版、OVA 等 · 按上映日期排列", vm.ListCaption);
        Assert.Equal(0, source.CurrentSeasonRequests);
        Assert.Null(vm.RetryOthersCommand.ExecutionTask);
    }

    [Fact]
    public async Task Reload_NonEmptySchedule_ResetsUnavailableOtherState()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource(seasonFails: false) { IsScheduleEmpty = true };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        await vm.PendingOthersLoad;
        await vm.PendingTimeMachineLoad;
        vm.SelectDay(CalendarDay.OtherKey);
        Assert.Equal("–", vm.Days.Single(day => day.IsOther).CountText);
        Assert.Equal(0, source.CurrentSeasonRequests);

        source.IsScheduleEmpty = false;
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        await vm.PendingOthersLoad;
        await vm.PendingTimeMachineLoad;

        Assert.True(vm.HasData);
        Assert.Equal(1, source.CurrentSeasonRequests);
        Assert.Equal("1 部", vm.Days.Single(day => day.IsOther).CountText);
        Assert.Equal(10, Assert.Single(vm.VisibleEntries).Anime.ID);
        Assert.Equal("本季没有其他作品", vm.EmptyText);
        Assert.Equal("网络动画、剧场版、OVA 等 · 按上映日期排列", vm.ListCaption);
    }

    [Fact]
    public async Task SlowCatalog_UnknownItemsStayInOtherThenMoveWithoutLosingSavingStatus()
    {
        await RunProductionMigrationAsync();
        var gate = new TaskCompletionSource();
        var source = new ScheduleSource(seasonFails: false)
        {
            SeasonGate = gate,
            Schedule = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Unknown),
                ScheduleSource.Item(2, ScheduleSource.Today, AnimeMediaFormat.Unknown)],
            Catalog = [ScheduleSource.Item(1, null, AnimeMediaFormat.Television),
                ScheduleSource.Item(2, null, AnimeMediaFormat.Ona)],
        };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.Empty(vm.VisibleEntries);
        Assert.Equal("正在确认本季 TV 动画的放送排期…", vm.EmptyText);
        Assert.Null(vm.DailyPick);
        vm.SelectDay(CalendarDay.OtherKey);
        Assert.Equal(2, vm.VisibleEntries.Count);
        var tv = vm.VisibleEntries.Single(entry => entry.Anime.ID == 1);
        var ona = vm.VisibleEntries.Single(entry => entry.Anime.ID == 2);
        tv.IsSavingStatus = true;
        tv.Status = AnimeTrackingStatus.Watching;

        gate.SetResult();
        await vm.PendingOthersLoad;

        Assert.Same(ona, Assert.Single(vm.VisibleEntries));
        Assert.True(ona.IsOther);
        Assert.Equal("网络", ona.ShelfBadgeText);
        Assert.Equal("1 部", vm.Days.Single(day => day.IsOther).CountText);
        vm.SelectDay(ScheduleSource.Today);
        Assert.Same(tv, Assert.Single(vm.VisibleEntries));
        Assert.False(tv.IsOther);
        Assert.True(tv.IsSavingStatus);
        Assert.Equal(AnimeTrackingStatus.Watching, tv.Status);
        Assert.Null(tv.ShelfBadgeText);
        Assert.Equal(AnimeMediaFormat.Unknown, source.Schedule![0].MediaFormat);
    }

    [Fact]
    public async Task MissingFormatsAndCatalogOnlyTvStayInOtherAndStatusReloadKeepsGrouping()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource(seasonFails: false)
        {
            Schedule = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Unknown),
                ScheduleSource.Item(2, ScheduleSource.Today, AnimeMediaFormat.Unknown),
                ScheduleSource.Item(3, ScheduleSource.Today, AnimeMediaFormat.Unknown),
                ScheduleSource.Item(4, ScheduleSource.Today, AnimeMediaFormat.Ona)],
            Catalog = [ScheduleSource.Item(1, null, AnimeMediaFormat.Television),
                ScheduleSource.Item(2, null, AnimeMediaFormat.Unknown),
                ScheduleSource.Item(5, null, AnimeMediaFormat.Television)],
        };
        var vm = await LoadAsync(source);

        Assert.Equal(1, Assert.Single(vm.VisibleEntries).Anime.ID);
        Assert.Equal(1, vm.DailyPick?.Anime.ID);
        vm.SelectDay(CalendarDay.OtherKey);
        Assert.Equal(new[] { 2, 3, 4, 5 }, vm.VisibleEntries.Select(entry => entry.Anime.ID));
        await vm.ReloadStatusesAsync();
        Assert.All(vm.VisibleEntries, entry => Assert.True(entry.IsOther));
        Assert.Equal("5", vm.TotalCountText);
        Assert.Equal("4 部", vm.Days.Single(day => day.IsOther).CountText);
        Assert.Equal(1, vm.DailyPick?.Anime.ID);

        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(4, AnimeTrackingStatus.Blocked);
        await vm.ReloadStatusesAsync();
        Assert.DoesNotContain(vm.VisibleEntries, entry => entry.Anime.ID == 4);
        await tracking.RemoveStatusAsync(4);
        await vm.ReloadStatusesAsync();
        Assert.True(vm.VisibleEntries.Single(entry => entry.Anime.ID == 4).IsOther);
        Assert.Equal("4 部", vm.Days.Single(day => day.IsOther).CountText);
    }

    [Fact]
    public async Task CatalogReclassification_ReplacesStoredNonTvDailyPickWithConfirmedTv()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SaveCalendarDailyPickAsync(DateOnly.FromDateTime(DateTime.Today), 1);
        var gate = new TaskCompletionSource();
        var source = new ScheduleSource(seasonFails: false)
        {
            SeasonGate = gate,
            Schedule = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Television),
                ScheduleSource.Item(2, ScheduleSource.Today, AnimeMediaFormat.Unknown)],
            Catalog = [ScheduleSource.Item(1, null, AnimeMediaFormat.Ona),
                ScheduleSource.Item(2, null, AnimeMediaFormat.Television)],
        };
        var vm = new CurrentSeasonViewModel(source, tracking);
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Assert.Equal(1, vm.DailyPick?.Anime.ID);

        gate.SetResult();
        await vm.PendingOthersLoad;

        Assert.Equal(2, Assert.Single(vm.VisibleEntries).Anime.ID);
        Assert.Equal(2, vm.DailyPick?.Anime.ID);
        Assert.Equal((DateOnly.FromDateTime(DateTime.Today), 2), await tracking.LoadCalendarDailyPickAsync());
        vm.SelectDay(CalendarDay.OtherKey);
        Assert.Equal(1, Assert.Single(vm.VisibleEntries).Anime.ID);
    }

    [Fact]
    public async Task FailedCatalog_UnknownItemsRemainVisibleInOtherAndRetryConfirmsTv()
    {
        await RunProductionMigrationAsync();
        var source = new ScheduleSource(seasonFails: true)
        {
            Schedule = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Unknown)],
        };
        var vm = await LoadAsync(source);
        Assert.Empty(vm.VisibleEntries);
        vm.SelectDay(CalendarDay.OtherKey);
        var entry = Assert.Single(vm.VisibleEntries);
        Assert.True(entry.IsOther);
        Assert.Equal("–", vm.Days.Single(day => day.IsOther).CountText);
        source.SeasonFails = false;

        await vm.RetryOthersCommand.ExecuteAsync(null);
        vm.SelectDay(ScheduleSource.Today);

        Assert.Same(entry, Assert.Single(vm.VisibleEntries));
        Assert.False(entry.IsOther);
        Assert.Equal(1, vm.DailyPick?.Anime.ID);
    }

    [Fact]
    public async Task CancelledCatalog_LateResponseDoesNotReclassifyUntilReturn()
    {
        await RunProductionMigrationAsync();
        var gate = new TaskCompletionSource();
        var source = new ScheduleSource(seasonFails: false)
        {
            SeasonGate = gate,
            Schedule = [ScheduleSource.Item(1, ScheduleSource.Today, AnimeMediaFormat.Unknown)],
        };
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory));
        await vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        var cancelledLoad = vm.PendingOthersLoad;
        vm.SelectDay(CalendarDay.OtherKey);
        var entry = Assert.Single(vm.VisibleEntries);
        vm.CancelSupplementaryLoads();
        // 替身刻意不响应取消，让旧结果在离开页面后才返回。
        gate.SetResult();
        await cancelledLoad;
        Assert.True(entry.IsOther);
        Assert.Equal(AnimeMediaFormat.Unknown, entry.Anime.MediaFormat);

        vm.ResumeSupplementaryLoads();
        await vm.PendingOthersLoad;
        vm.SelectDay(ScheduleSource.Today);

        Assert.Same(entry, Assert.Single(vm.VisibleEntries));
        Assert.False(entry.IsOther);
        Assert.Equal(1, vm.DailyPick?.Anime.ID);
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
        public static readonly int Today =
            AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        /// <summary>设置后，本季的按季查询等它完成才返回（番剧时光机查的往年不受影响）。</summary>
        public TaskCompletionSource? SeasonGate { get; init; }

        public int CurrentSeasonRequests { get; private set; }

        public bool IsScheduleEmpty { get; set; }

        public bool SeasonFails { get; set; } = seasonFails;

        public List<Anime>? Schedule { get; init; }

        public List<Anime>? Catalog { get; init; }

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult<List<Anime>>(IsScheduleEmpty
                ? []
                : Schedule ?? [Item(1, Today, AnimeMediaFormat.Television)]);

        // 按季查询同时返回周更作品和一部剧场版，只有剧场版应归入“其他”。
        public async Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
        {
            if (year == SeasonHelper.GetCurrentSeason().year)
            {
                CurrentSeasonRequests++;
                if (SeasonGate is { } gate)
                    await gate.Task;
            }

            return SeasonFails
                ? throw new HttpRequestException("offline")
                : Catalog ?? [Item(1, Today, AnimeMediaFormat.Television), Item(10, null, AnimeMediaFormat.Movie)];
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

        public static Anime Item(int id, int? weekday, AnimeMediaFormat format) => new(
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
