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

    [Fact]
    public async Task DisabledStartupCancelsOldSchedulesAndKeepsPendingRecords()
    {
        var action = new ActionCenterService(DbFactory);
        await action.UpsertPlanAsync(40, "保留的计划", AnimePlanPriority.Normal, null, 0);
        var reminder = Reminder(40, DateTimeOffset.Now.AddHours(-1));
        await action.AddReminderAsync(reminder);
        var notifications = new FakeNotificationService();
        using var coordinator = new PlanReminderCoordinator(action, notifications, new FakeNavigator(), initiallyEnabled: false);
        await coordinator.ReconcileAsync();
        Assert.Empty(notifications.Requests);
        Assert.Equal("anime-plan-40", Assert.Single(notifications.CancelledGroups));
        var saved = Assert.Single(await action.GetRemindersAsync());
        Assert.Equal(PlanReminderState.Pending, saved.State);
        Assert.Null(saved.CatchUpSentAt);
        Assert.NotNull(await action.GetPlanAsync(40));
    }

    [Fact]
    public async Task DisableWaitsForAcceptedScheduleThenCancelsItAndBlocksReschedule()
    {
        var action = new ActionCenterService(DbFactory);
        await action.UpsertPlanAsync(41, "在途提醒", AnimePlanPriority.Normal, null, 0);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new FakeNotificationService { BeforeSchedule = () => { entered.TrySetResult(); return release.Task; } };
        using var coordinator = new PlanReminderCoordinator(action, notifications, new FakeNavigator());
        var plan = (await action.GetPlanAsync(41))!;
        var adding = coordinator.AddAbsoluteReminderAsync(plan, DateTimeOffset.Now.AddDays(1));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabling = coordinator.SetEnabledAsync(false);
        try { Assert.False(disabling.IsCompleted); }
        finally { release.TrySetResult(); }
        await adding;
        await disabling;
        Assert.Empty(notifications.Scheduled);
        await coordinator.RescheduleAnimeAsync(plan);
        Assert.Single(notifications.Requests);
        Assert.Equal(PlanReminderState.Pending, Assert.Single(await action.GetRemindersAsync()).State);
    }

    [Fact]
    public async Task ReenableSchedulesFutureAndOnlyCatchesUpWithinExistingWindow()
    {
        var action = new ActionCenterService(DbFactory);
        await action.UpsertPlanAsync(42, "重新启用", AnimePlanPriority.Normal, null, 0);
        var future = Reminder(42, DateTimeOffset.Now.AddDays(1));
        var recent = Reminder(42, DateTimeOffset.Now.AddHours(-1));
        var old = Reminder(42, DateTimeOffset.Now.AddDays(-2));
        foreach (var reminder in new[] { future, recent, old }) await action.AddReminderAsync(reminder);
        var notifications = new FakeNotificationService();
        using var coordinator = new PlanReminderCoordinator(action, notifications, new FakeNavigator(), initiallyEnabled: false);
        await coordinator.ReconcileAsync();
        await coordinator.SetEnabledAsync(true);
        Assert.Equal(new[] { future.ReminderId, recent.ReminderId }.Order(), notifications.Requests.Select(item => item.Tag).Order());
        var saved = await action.GetRemindersAsync();
        Assert.NotNull(saved.Single(item => item.ReminderId == recent.ReminderId).CatchUpSentAt);
        Assert.Null(saved.Single(item => item.ReminderId == old.ReminderId).CatchUpSentAt);
    }

    [Fact]
    public async Task DisabledSnoozeKeepsTheOriginalReminderAndDoesNotCreateAnotherSchedule()
    {
        var action = new ActionCenterService(DbFactory);
        await action.UpsertPlanAsync(43, "关闭后稍后提醒", AnimePlanPriority.Normal, null, 0);
        var notifications = new FakeNotificationService();
        var navigation = new FakeNavigator();
        using var coordinator = new PlanReminderCoordinator(action, notifications, navigation);
        var reminder = await coordinator.AddAbsoluteReminderAsync((await action.GetPlanAsync(43))!, DateTimeOffset.Now.AddDays(1));
        await coordinator.SetEnabledAsync(false);
        await notifications.ActivateAsync(new(PlanReminderCoordinator.NotificationCategory, "snooze",
            new Dictionary<string, string> { ["animeId"] = "43", ["reminderId"] = reminder.ReminderId }));
        Assert.Single(notifications.Requests);
        Assert.Empty(notifications.Scheduled);
        Assert.Equal(PlanReminderState.Pending, Assert.Single(await action.GetRemindersAsync()).State);
        Assert.Equal(43, navigation.Parameter);
    }

    [Fact]
    public async Task RapidEnableThenDisableFinishesInAcceptedOrder()
    {
        var action = new ActionCenterService(DbFactory);
        await action.UpsertPlanAsync(44, "快速开关", AnimePlanPriority.Normal, null, 0);
        await action.AddReminderAsync(Reminder(44, DateTimeOffset.Now.AddDays(1)));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = new FakeNotificationService { BeforeSchedule = () => { entered.TrySetResult(); return release.Task; } };
        using var coordinator = new PlanReminderCoordinator(action, notifications, new FakeNavigator(), initiallyEnabled: false);
        var enabling = coordinator.SetEnabledAsync(true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disabling = coordinator.SetEnabledAsync(false);
        release.SetResult();
        await Task.WhenAll(enabling, disabling);
        Assert.Empty(notifications.Scheduled);
        await coordinator.ReconcileAsync();
        Assert.Single(notifications.Requests);
    }

    private static PlanReminder Reminder(int id, DateTimeOffset scheduled)
        => new(Guid.NewGuid().ToString("N"), id, PlanReminderKind.Absolute, null, null, scheduled, scheduled,
            PlanReminderState.Pending, null, null);

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

        internal Func<Task>? BeforeSchedule { get; set; }
        internal HashSet<string> Scheduled { get; } = [];

        public async Task ScheduleAsync(
            AppNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            if (BeforeSchedule is { } before) await before();
            Requests.Add(request);
            Scheduled.Add(request.Tag);
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
            foreach (var request in Requests.Where(item => item.Group == group)) Scheduled.Remove(request.Tag);
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
