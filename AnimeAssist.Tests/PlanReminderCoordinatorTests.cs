using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PlanReminderCoordinatorTests : DbTestBase
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, -1)]
    [InlineData(365, -365)]
    public void GetRelativeDayOffset_ValidInteger_ReturnsNegativeOffset(
        double value,
        int expected)
        => Assert.Equal(
            expected,
            PlanReminderCoordinator.GetRelativeDayOffset(value));

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(1.5)]
    [InlineData(366)]
    public void GetRelativeDayOffset_InvalidValue_Throws(double value)
        => Assert.Throws<InvalidOperationException>(
            () => PlanReminderCoordinator.GetRelativeDayOffset(value));

    [Fact]
    public async Task RelativeReminder_ReschedulesWhenTargetDateChanges()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var notifications = new FakeNotificationService();
        using var coordinator = new PlanReminderCoordinator(
            actionCenter,
            notifications,
            new FakeNavigator());
        await actionCenter.UpsertPlanAsync(
            10,
            "测试番剧",
            AnimePlanPriority.Normal,
            DateOnly.FromDateTime(DateTime.Today.AddDays(5)),
            0);
        var plan = await actionCenter.GetPlanAsync(10);
        Assert.NotNull(plan);
        await coordinator.AddRelativeReminderAsync(
            plan!,
            -1,
            new TimeOnly(20, 0));
        var originalDelivery = notifications.Requests.Single().DeliveryTime;

        await actionCenter.UpsertPlanAsync(
            10,
            "测试番剧",
            AnimePlanPriority.Normal,
            DateOnly.FromDateTime(DateTime.Today.AddDays(8)),
            0);
        var updated = await actionCenter.GetPlanAsync(10);
        await coordinator.RescheduleAnimeAsync(updated!);

        Assert.Equal(
            originalDelivery.AddDays(3),
            notifications.Requests.Last().DeliveryTime);
    }

    [Fact]
    public async Task StartAction_UpdatesTrackingAndCancelsPlanNotifications()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var notifications = new FakeNotificationService();
        var navigator = new FakeNavigator();
        using var coordinator = new PlanReminderCoordinator(
            actionCenter,
            notifications,
            navigator);
        await actionCenter.UpsertPlanAsync(
            20,
            "测试番剧",
            AnimePlanPriority.High,
            null,
            0);
        var plan = await actionCenter.GetPlanAsync(20);
        var reminder = await coordinator.AddAbsoluteReminderAsync(
            plan!,
            DateTimeOffset.Now.AddDays(1));

        await notifications.ActivateAsync(
            new AppNotificationActivation(
                PlanReminderCoordinator.NotificationCategory,
                "start",
                new Dictionary<string, string>
                {
                    ["animeId"] = "20",
                    ["reminderId"] = reminder.ReminderId,
                }));

        var status = await new TrackingService(DbFactory).GetStatusAsync(20);
        Assert.Equal(AnimeTrackingStatus.Watching, status);
        Assert.Contains("anime-plan-20", notifications.CancelledGroups);
        Assert.Equal(20, navigator.Parameter);
    }

    [Fact]
    public async Task SnoozeAction_RepeatedActivationCreatesOneReminder()
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        var notifications = new FakeNotificationService();
        using var coordinator = new PlanReminderCoordinator(
            actionCenter,
            notifications,
            new FakeNavigator());
        await actionCenter.UpsertPlanAsync(
            30,
            "测试番剧",
            AnimePlanPriority.Normal,
            null,
            0);
        var plan = await actionCenter.GetPlanAsync(30);
        var reminder = await coordinator.AddAbsoluteReminderAsync(
            plan!,
            DateTimeOffset.Now.AddDays(1));
        var activation = new AppNotificationActivation(
            PlanReminderCoordinator.NotificationCategory,
            "snooze",
            new Dictionary<string, string>
            {
                ["animeId"] = "30",
                ["reminderId"] = reminder.ReminderId,
            });

        await notifications.ActivateAsync(activation);
        await notifications.ActivateAsync(activation);

        var reminders = await actionCenter.GetRemindersAsync(animeId: 30);
        Assert.Equal(2, reminders.Count);
        Assert.Equal(2, notifications.Requests.Count);
    }

    private sealed class FakeNotificationService :
        IAppNotificationService
    {
        private Func<
            AppNotificationActivation,
            CancellationToken,
            Task>? _handler;

        public bool IsSupported => true;

        public bool NotificationsEnabled => true;

        public List<AppNotificationRequest> Requests { get; } = [];

        public List<string> CancelledGroups { get; } = [];

        public Task ScheduleAsync(
            AppNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task CancelAsync(
            string group,
            string tag,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task CancelGroupAsync(
            string group,
            CancellationToken cancellationToken = default)
        {
            CancelledGroups.Add(group);
            return Task.CompletedTask;
        }

        public IDisposable RegisterActivationHandler(
            string category,
            Func<
                AppNotificationActivation,
                CancellationToken,
                Task> handler)
        {
            _handler = handler;
            return new Registration(() => _handler = null);
        }

        public Task OpenNotificationSettingsAsync()
            => Task.CompletedTask;

        public Task ActivateAsync(AppNotificationActivation activation)
            => (_handler ?? throw new InvalidOperationException(
                "未注册通知处理器。"))(
                    activation,
                    CancellationToken.None);

        private sealed class Registration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }

    private sealed class FakeNavigator : IPluginNavigator
    {
        public object? Parameter { get; private set; }

        public void Navigate(Type pageType, object? parameter = null)
            => Parameter = parameter;
    }
}
