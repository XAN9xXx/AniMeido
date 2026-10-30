using AniMeido.Contracts;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PlanReminderCoordinatorTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory DbFactory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

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
        var targetDate = DateOnly.FromDateTime(DateTime.Today).AddDays(5);
        var reminderTime = new TimeOnly(20, 0);
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
            targetDate,
            0);
        var plan = await actionCenter.GetPlanAsync(10);
        Assert.NotNull(plan);
        await coordinator.AddRelativeReminderAsync(
            plan!,
            -1,
            reminderTime);
        var originalDelivery = notifications.Requests.Single().DeliveryTime;
        Assert.Equal(
            new DateTimeOffset(targetDate.AddDays(-1).ToDateTime(reminderTime)),
            originalDelivery);

        var updatedTargetDate = targetDate.AddDays(3);
        await actionCenter.UpsertPlanAsync(
            10,
            "测试番剧",
            AnimePlanPriority.Normal,
            updatedTargetDate,
            0);
        var updated = await actionCenter.GetPlanAsync(10);
        await coordinator.RescheduleAnimeAsync(updated!);

        Assert.Equal(
            new DateTimeOffset(updatedTargetDate.AddDays(-1).ToDateTime(reminderTime)),
            notifications.Requests.Last().DeliveryTime);
    }

    [Fact]
    public async Task LegacyStartAction_OnlyOpensPlan()
    {
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
        Assert.DoesNotContain(
            notifications.Requests.Single().Actions,
            action => action.Action == "start");

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
        Assert.Null(status);
        var currentPlan = await actionCenter.GetPlanAsync(20);
        Assert.NotNull(currentPlan);
        Assert.Null(currentPlan.StartedAt);
        Assert.Null(currentPlan.ArchivedAt);
        Assert.Empty(notifications.CancelledGroups);
        Assert.Equal(
            PlanReminderState.Handled,
            Assert.Single(await actionCenter.GetRemindersAsync(animeId: 20)).State);
        Assert.Equal(20, navigator.Parameter);
    }

    [Fact]
    public async Task SnoozeAction_RepeatedActivationCreatesOneReminder()
    {
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

    private sealed class FakeNavigator : IPlanReminderNavigation
    {
        public object? Parameter { get; private set; }

        public void OpenPlan(int animeId)
            => Parameter = animeId;
    }
}
