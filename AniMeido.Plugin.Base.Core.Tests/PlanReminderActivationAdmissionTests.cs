using System.Collections.Concurrent;
using AniMeido.Contracts;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PlanReminderActivationAdmissionTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Snooze_KeepsOneAdmissionBetweenHandlingAndCreatingTheReplacement()
    {
        var service = new ActionCenterService(_database.Factory);
        await service.UpsertPlanAsync(42, "计划", AnimePlanPriority.Normal, null, 0);
        var notifications = new NotificationStub();
        using var coordinator = new PlanReminderCoordinator(service, notifications, new NavigationStub());
        var plan = Assert.IsType<AnimePlan>(await service.GetPlanAsync(42));
        var original = await coordinator.AddAbsoluteReminderAsync(plan, DateTimeOffset.Now.AddDays(1));
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedContext();
        var started = Task.Run(() =>
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                return new ActivationTask(notifications.ActivateAsync(new AppNotificationActivation(
                    PlanReminderCoordinator.NotificationCategory, "snooze",
                    new Dictionary<string, string>
                    {
                        ["animeId"] = "42",
                        ["reminderId"] = original.ReminderId,
                    })));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        });
        PersonalDataAccess.Operation? import = null;
        Task? activation = null;
        try
        {
            activation = (await started.WaitAsync(Budget)).Value;
            blocker.Dispose();
            await context.Posted.Task.WaitAsync(Budget);
            import = _database.Factory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(CancellationToken.None);
            Assert.False(drained.IsCompleted);
            context.Resume();
            await activation.WaitAsync(Budget);
            await drained.WaitAsync(Budget);
        }
        finally
        {
            blocker.Dispose();
            import?.Dispose();
            context.Resume();
            activation ??= (await started.WaitAsync(Budget)).Value;
            await activation.WaitAsync(Budget);
        }

        var reminders = await service.GetRemindersAsync(42);
        Assert.Equal(2, reminders.Count);
        Assert.Equal(PlanReminderState.Handled, reminders.Single(r => r.ReminderId == original.ReminderId).State);
        Assert.Single(reminders, r => r.State == PlanReminderState.Pending);
        Assert.Equal(2, notifications.Scheduled);
    }

    private sealed record ActivationTask(Task Value);

    private sealed class PausedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly object _sync = new();
        private bool _resumed;
        internal TaskCompletionSource Posted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_sync)
            {
                if (_resumed)
                    ThreadPool.QueueUserWorkItem(_ => callback(state));
                else
                    _queue.Enqueue((callback, state));
            }
            Posted.TrySetResult();
        }

        internal void Resume()
        {
            lock (_sync)
            {
                _resumed = true;
                while (_queue.TryDequeue(out var item))
                    ThreadPool.QueueUserWorkItem(_ => item.Callback(item.State));
            }
        }
    }

    private sealed class NavigationStub : IPlanReminderNavigation
    {
        public void OpenPlan(int animeId) { }
    }

    private sealed class NotificationStub : IAppNotificationService
    {
        private Func<AppNotificationActivation, CancellationToken, Task>? _handler;
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public int Scheduled { get; private set; }
        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
        {
            Scheduled++;
            return Task.CompletedTask;
        }
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        public IDisposable RegisterActivationHandler(string category,
            Func<AppNotificationActivation, CancellationToken, Task> handler)
        {
            _handler = handler;
            return new Registration(() => _handler = null);
        }
        internal Task ActivateAsync(AppNotificationActivation activation)
            => (_handler ?? throw new InvalidOperationException())(activation, CancellationToken.None);
        private sealed class Registration(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}
