using AniMeido.Contracts.Notifications;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifetime_LateReminderResultCannotReplaceCurrentNotification(bool replace, bool fail)
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "本地计划", AnimePlanPriority.Normal, null, 0);
        var scheduledFor = DateTimeOffset.Now.AddDays(1);
        await actionCenter.AddReminderAsync(new PlanReminder(
            "lifetime-reminder-80", 80, PlanReminderKind.Absolute, null, null,
            scheduledFor, scheduledFor, PlanReminderState.Pending, null, null));
        var entered = NewCompletion();
        var scheduling = NewCompletion();
        var calls = 0;
        var notifications = new LifetimeNotifications
        {
            Schedule = () =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return Task.CompletedTask;

                entered.TrySetResult();
                return scheduling.Task;
            },
        };
        using var reminders = new PlanReminderCoordinator(actionCenter, notifications, new NoopNavigator());
        var source = new OfflineSource
        {
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (id, _) => Task.FromResult<Anime?>(Broadcast(id)),
        };
        var vm = new TodayViewModel(source, new TrackingService(DbFactory), _featureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), NullLogger.Instance)
        {
            NotificationMessage = "上次的通知结果",
        };
        using var cancellation = new CancellationTokenSource();
        var loading = vm.LoadAsync(cancellation.Token);
        var firstTheme = vm.Theme.PendingLoad;
        Task replacement = Task.CompletedTask;
        var expected = vm.NotificationMessage;
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (replace)
            {
                notifications.IsSupported = false;
                replacement = vm.LoadAsync();
                await replacement.WaitAsync(LoggingBudget);
                expected = vm.NotificationMessage;
                Assert.Equal("系统通知不可用，补番计划只会显示在今天页。", expected);
            }
            else
            {
                cancellation.Cancel();
            }

            // 通知能力可能在等待期间变化；只有当前加载可以据此更新页面提示。
            notifications.IsSupported = true;
            if (fail)
                scheduling.SetException(new InvalidOperationException("旧加载的提醒结果"));
            else
                scheduling.SetResult();
            await loading.WaitAsync(LoggingBudget);

            Assert.Equal(expected, vm.NotificationMessage);
            Assert.Null(vm.ErrorMessage);
            Assert.False(vm.IsLoading);
            Assert.Equal(!replace, vm.IsLoadInterrupted);
        }
        finally
        {
            scheduling.TrySetResult();
            await Task.WhenAll(loading, replacement).WaitAsync(LoggingBudget);
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            await Task.WhenAll(firstTheme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    private sealed class LifetimeNotifications : IAppNotificationService
    {
        public bool IsSupported { get; set; } = true;
        public bool NotificationsEnabled => true;
        public required Func<Task> Schedule { get; init; }

        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => Schedule();
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
}
