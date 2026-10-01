using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed class TodayViewModelLoadTests : DbTestBase
{
    [Fact]
    public async Task CalendarTvOnlyGrouping_DoesNotFilterTodayBroadcasts()
    {
        await RunProductionMigrationAsync();
        var today = AnimeListPresentation.ToBangumiWeekday(DateTime.Today.DayOfWeek);
        Anime Item(int id, AnimeMediaFormat format, int weekday) => new(
            id, $"作品{id}", null, [], DateOnly.FromDateTime(DateTime.Today), null,
            string.Empty, DateTime.Today.Year, DateTime.Today.Month, weekday, MediaFormat: format);
        var source = new OfflineSource
        {
            Schedule = [Item(1, AnimeMediaFormat.Television, today),
                Item(2, AnimeMediaFormat.Ona, today),
                Item(3, AnimeMediaFormat.Ova, today),
                Item(4, AnimeMediaFormat.Movie, today),
                Item(5, AnimeMediaFormat.Unknown, today),
                Item(6, AnimeMediaFormat.Television, today % 7 + 1)],
        };
        var tracking = new TrackingService(DbFactory);
        var calendar = new CurrentSeasonViewModel(source, tracking);
        await calendar.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        await calendar.PendingOthersLoad;
        Assert.Equal(1, Assert.Single(calendar.VisibleEntries).Anime.ID);
        calendar.SelectDay(CalendarDay.OtherKey);
        Assert.Equal(new[] { 2, 3, 4, 5 }, calendar.VisibleEntries.Select(entry => entry.Anime.ID));

        var actionCenter = new ActionCenterService(DbFactory);
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(
            source, tracking, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory));
        await vm.LoadAsync();

        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, vm.AllBroadcasts.Select(anime => anime.ID));
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task DetailFailure_StillShowsLocalPlansWithoutCover()
    {
        // 离线且没有详情缓存时，封面拿不到，但本地补番计划必须照常显示。
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var tracking = new TrackingService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "离线老番", AnimePlanPriority.Normal, null, 0);
        await tracking.SetStatusAsync(80, AnimeTrackingStatus.PlanToWatch);
        using var reminders = new PlanReminderCoordinator(
            actionCenter,
            new NoopNotificationService(),
            new NoopNavigator());
        var vm = new TodayViewModel(
            new OfflineSource(),
            tracking,
            actionCenter,
            reminders,
            new BrowseHistoryService(DbFactory),
            new ArchiveService(DbFactory));

        await vm.LoadAsync();

        var entry = Assert.Single(vm.Plans);
        Assert.Equal(80, entry.Plan.AnimeId);
        Assert.Null(entry.Anime);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task NewCatchUpStatusAfterFirstLoad_AppearsOnNextLoad()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var tracking = new TrackingService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "原有计划", AnimePlanPriority.Normal, null, 0);
        await tracking.SetStatusAsync(80, AnimeTrackingStatus.PlanToWatch);
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(
            new OfflineSource(), tracking, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory));

        await vm.LoadAsync();
        Assert.Single(vm.Plans);

        // 关注管理新增补番状态时没有对应计划记录；下一次加载必须补齐。
        await tracking.SetStatusAsync(81, AnimeTrackingStatus.PlanToWatch);
        await vm.LoadAsync();

        Assert.Equal(2, vm.Plans.Count);
        Assert.Contains(vm.Plans, entry => entry.Plan.AnimeId == 81);
        Assert.Equal(2, (await actionCenter.GetPlansAsync()).Count);
    }

    [Fact]
    public async Task Reload_KeepsPlanCollectionWhenNothingChanged()
    {
        // 整体替换计划集合会重建全部卡片并收起卡包；内容没变时必须保留原集合。
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(80, AnimeTrackingStatus.PlanToWatch);
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(
            new OfflineSource(), tracking, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory));

        await vm.LoadAsync();
        var plans = vm.Plans;
        await vm.LoadAsync();

        Assert.Same(plans, vm.Plans);
        Assert.Single(vm.Plans);
    }

    [Fact]
    public async Task RefreshAfterStatusChange_ShowsNewPlan()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(80, AnimeTrackingStatus.PlanToWatch);
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(
            new OfflineSource(), tracking, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory));
        await vm.LoadAsync();

        // 今日主题里点“补番”会写入补番状态并建立计划。
        await tracking.SetStatusAsync(81, AnimeTrackingStatus.PlanToWatch);
        await vm.RefreshAfterStatusChangeAsync();

        Assert.Equal(2, vm.Plans.Count);
        Assert.Contains(vm.Plans, entry => entry.Plan.AnimeId == 81);
    }

    [Fact]
    public async Task CancelledLoad_IsReportedAsInterruptedUntilNextFullLoad()
    {
        // 离开页面会取消主加载；按原实例返回时靠这个标记决定是否重新加载。
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var tracking = new TrackingService(DbFactory);
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(
            new OfflineSource(), tracking, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await vm.LoadAsync(cancellation.Token);
        Assert.True(vm.IsLoadInterrupted);

        await vm.LoadAsync();
        Assert.False(vm.IsLoadInterrupted);
    }

    private sealed class OfflineSource : IAnimeDataSource
    {
        public List<Anime> Schedule { get; init; } = [];

        private static Task<T> Offline<T>()
            => Task.FromException<T>(new BangumiApiException("offline"));

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Offline<List<Anime>>();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Task.FromResult(Schedule);
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Offline<Anime?>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Offline<List<Studio>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Offline<List<Tag>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Offline<List<VoiceActor>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Offline<List<CharacterRole>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Offline<List<PersonWork>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Offline<(List<Anime>, int)>();
        public Task<(List<Anime> Results, int Total)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Offline<(List<Anime>, int)>();
    }

    private sealed class NoopNotificationService : IAppNotificationService
    {
        public bool IsSupported => true;

        public bool NotificationsEnabled => true;

        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public IDisposable RegisterActivationHandler(
            string category,
            Func<AppNotificationActivation, CancellationToken, Task> handler)
            => new Registration();

        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NoopNavigator : IPluginNavigator
    {
        public void Navigate(Type pageType, object? parameter = null)
        {
        }
    }
}
