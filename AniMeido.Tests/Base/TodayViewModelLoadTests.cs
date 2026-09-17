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
            new BrowseHistoryService(DbFactory));

        await vm.LoadAsync();

        var entry = Assert.Single(vm.Plans);
        Assert.Equal(80, entry.Plan.AnimeId);
        Assert.Null(entry.Anime);
        Assert.Null(vm.ErrorMessage);
    }

    private sealed class OfflineSource : IAnimeDataSource
    {
        private static Task<T> Offline<T>()
            => Task.FromException<T>(new BangumiApiException("offline"));

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => Offline<List<Anime>>();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Task.FromResult(new List<Anime>());
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
