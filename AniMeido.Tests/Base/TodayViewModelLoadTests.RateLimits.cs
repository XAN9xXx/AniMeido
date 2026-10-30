using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    [Fact]
    public async Task RateLimitedScheduleKeepsTheLocalPlanAndShowsTheRetryNotice()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "本地计划", AnimePlanPriority.Normal, null, 0);
        var source = new OfflineSource
        {
            ScheduleRequest = _ => Task.FromException<List<Anime>>(MetadataServiceException.RateLimited()),
        };
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(source, new TrackingService(DbFactory), _featureState,
            actionCenter, reminders, new BrowseHistoryService(DbFactory),
            new ArchiveService(DbFactory), NullLogger.Instance);

        await LoadAndDrainThemeAsync(vm);

        Assert.Equal(80, Assert.Single(vm.Plans).Plan.AnimeId);
        Assert.Equal("请求较多，请稍后重试。", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }
}
