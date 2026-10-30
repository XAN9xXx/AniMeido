using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayPlanDetailDemandTests(ITestOutputHelper output) : DbTestBase
{
    private PersonalFeatureStateStore? _featureState;
    private PersonalFeatureStateStore FeatureState => _featureState ??= new(DbFactory);

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task Load_WithLargeExistingPlanSet_ResolvesOnlyFoldedDeckDetailsAfterGrouping(int planCount)
    {
        await RunProductionMigrationAsync();
        var today = DateOnly.FromDateTime(DateTime.Today);
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        {
            for (var id = 1; id <= planCount; id++)
            {
                var (priority, date) = PlanValues(id, today);
                await ActionCenterService.UpsertPlanInTransactionAsync(
                    connection, transaction, id, $"计划{id}", priority, date, id,
                    preserveSortOrder: false,
                    now: "2026-10-01T00:00:00.0000000+00:00",
                    cancellationToken: default);
            }
            transaction.Commit();
        }

        var source = new PlanSource();
        var actionCenter = new ActionCenterService(DbFactory);
        var originalPlans = await actionCenter.GetPlansAsync();
        // SQL 先按优先级给出未排期计划；页面分组后却必须先显示逾期计划。
        Assert.Equal(new[] { 4, 8, 12, 16, 20, 24 }, originalPlans.Take(6).Select(plan => plan.AnimeId));
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotifications(), new NoopNavigator());
        var vm = new TodayViewModel(
            source, new TrackingService(DbFactory), FeatureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), NullLogger.Instance);
        // 没有标记、浏览或评分记录；主题的目录查询均为空，播放器不启用。
        // 全部计划已经存在，因此不会把旧补番状态的计划补建误算成封面详情请求。
        try
        {
            await vm.LoadAsync();
            await vm.Theme.PendingLoad;
        }
        finally
        {
            vm.Theme.CancelLoad();
            await vm.Theme.PendingLoad;
        }

        var expectedOrder = new[] { 1, 2, 3, 0 }
            .SelectMany(group => Enumerable.Range(1, planCount).Where(id => id % 4 == group))
            .ToArray();
        Assert.Equal(planCount, vm.Plans.Count);
        Assert.Equal(expectedOrder, vm.Plans.Select(entry => entry.Plan.AnimeId));
        Assert.Equal($"{planCount} 部 · 按计划日期排列", vm.PlanCountText);
        Assert.All(vm.Plans, entry => Assert.Equal($"计划{entry.Plan.AnimeId}", entry.Title));
        Assert.Equal($"已逾期 · {planCount / 4}", vm.Plans[0].GroupHeader);
        Assert.Equal($"近期 · 七天内 · {planCount / 4}", vm.Plans[planCount / 4].GroupHeader);
        Assert.Equal($"之后 · {planCount / 4}", vm.Plans[planCount / 2].GroupHeader);
        Assert.Equal($"未排期 · {planCount / 4}", vm.Plans[planCount * 3 / 4].GroupHeader);
        Assert.Equal(originalPlans, await actionCenter.GetPlansAsync());
        Assert.Empty(await new TrackingService(DbFactory).GetAllTrackingAsync());
        Assert.False(vm.IsPlaybackAvailable);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsLoading);

        var requested = source.DetailRequests.ToArray();
        output.WriteLine($"Existing plans: {planCount}; detail calls: {requested.Length}; "
            + $"first requested IDs: {string.Join(", ", requested.Take(12))}");
        Assert.Equal(new[] { 1, 5, 9, 13, 17, 21 }, requested.Order());
        Assert.All(vm.Plans.Take(6), entry => Assert.Equal(entry.Plan.AnimeId, entry.Anime?.ID));
        Assert.All(vm.Plans.Skip(6), entry => Assert.Null(entry.Anime));
    }

    private static (AnimePlanPriority Priority, DateOnly? Date) PlanValues(int id, DateOnly today)
        => (id % 4) switch
        {
            0 => (AnimePlanPriority.Critical, null),
            1 => (AnimePlanPriority.Low, today.AddDays(-2)),
            2 => (AnimePlanPriority.High, today.AddDays(3)),
            _ => (AnimePlanPriority.Normal, today.AddDays(20)),
        };

    private sealed class PlanSource : IAnimeDataSource
    {
        public ConcurrentQueue<int> DetailRequests { get; } = new();
        public Func<int, CancellationToken, Task<Anime?>>? Detail { get; set; }

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DetailRequests.Enqueue(animeID);
            return Detail?.Invoke(animeID, ct) ?? Task.FromResult<Anime?>(PlanAnime(animeID));
        }

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Task.FromResult(new List<Anime>());
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct)
            => Task.FromResult(new List<Anime>());
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Studio>());
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<Tag>());
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<VoiceActor>());
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct)
            => Task.FromResult(new List<CharacterRole>());
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct)
            => Task.FromResult(new List<PersonWork>());
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort,
            CancellationToken ct, string? airDateFrom = null, string? airDateTo = null)
            => Task.FromResult((new List<Anime>(), 0));
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(
            string keyword, int offset, CancellationToken ct)
            => Task.FromResult((new List<Anime>(), 0, 0));
    }

    private sealed class NoopNotifications : IAppNotificationService
    {
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        public IDisposable RegisterActivationHandler(
            string category, Func<AppNotificationActivation, CancellationToken, Task> handler)
            => new Registration();

        private sealed class Registration : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NoopNavigator : IPlanReminderNavigation
    {
        public void OpenPlan(int animeId)
        {
        }
    }
}
