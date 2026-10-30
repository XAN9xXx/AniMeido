using System.Threading.Channels;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task Lifetime_InterruptedLoadWaitsForItsStartedBrowseRequest(bool replace)
    {
        await RunProductionMigrationAsync();
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "本地计划", AnimePlanPriority.Normal, null, 0);
        await new BrowseHistoryService(DbFactory).RecordAsync(301, "浏览记录301");
        var schedule = NewCompletion<List<Anime>>();
        var detail = NewCompletion<Anime?>();
        var detailEntered = NewCompletion();
        var plansPublished = NewCompletion();
        var context = new LifetimeLoadContext();
        var calls = 0;
        var detailCalls = 0;
        var themes = new List<Task>();
        var source = new OfflineSource
        {
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (id, _) =>
            {
                if (id != 301 || Interlocked.Increment(ref detailCalls) > 1)
                    return Task.FromResult<Anime?>(Broadcast(id));

                detailEntered.TrySetResult();
                return detail.Task;
            },
        };
        using var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(source, new TrackingService(DbFactory), _featureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), NullLogger.Instance);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TodayViewModel.Plans))
                plansPublished.TrySetResult();
        };
        source.ScheduleRequest = _ =>
        {
            themes.Add(vm.Theme.PendingLoad);
            vm.Theme.CancelLoad();
            if (Interlocked.Increment(ref calls) > 1)
                return Task.FromResult(new List<Anime> { Broadcast(2) });

            // 只扣住旧加载的续体。主题已经使用原上下文，不会混进本测试的单步调度。
            SynchronizationContext.SetSynchronizationContext(context);
            return schedule.Task;
        };
        using var cancellation = new CancellationTokenSource();
        Task loading;
        var previous = SynchronizationContext.Current;
        try
        {
            loading = vm.LoadAsync(cancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        Task replacement = Task.CompletedTask;
        var browseStarted = false;
        try
        {
            await context.AdvanceUntilAsync(Task.WhenAll(plansPublished.Task, detailEntered.Task));
            browseStarted = true;
            if (replace)
            {
                replacement = vm.LoadAsync();
                await replacement.WaitAsync(LoggingBudget);
                Assert.Equal("作品301", Assert.Single(vm.RecentBrowsed).Anime.Title);
            }
            else
            {
                cancellation.Cancel();
            }

            schedule.SetResult([Broadcast(1)]);
            // 已确认主流程等待日程；执行它的返回续体后，不能把仍被扣住的浏览请求丢下。
            await context.AdvanceOnceAsync();
            Assert.False(loading.IsCompleted);
            Assert.False(detail.Task.IsCompleted);
            Assert.Null(vm.ErrorMessage);
            if (replace)
            {
                Assert.Equal(2, Assert.Single(vm.AllBroadcasts).ID);
                Assert.False(vm.IsLoading);
                Assert.False(vm.IsLoadInterrupted);
            }
        }
        finally
        {
            schedule.TrySetResult([]);
            detail.TrySetResult(Broadcast(1));
            try
            {
                if (browseStarted)
                {
                    // 失败的旧实现已提前返回，也仍要执行真实浏览链的最终续体再清理数据库。
                    await context.AdvanceOnceAsync();
                }
            }
            finally
            {
                context.Resume();
                await Task.WhenAll(loading, replacement).WaitAsync(LoggingBudget);
                vm.SuspendPlanDetails();
                vm.Theme.CancelLoad();
                await Task.WhenAll(themes.Append(vm.Theme.PendingLoad)).WaitAsync(LoggingBudget);
            }
        }

        if (replace)
        {
            Assert.Equal("作品301", Assert.Single(vm.RecentBrowsed).Anime.Title);
            Assert.Equal(2, Assert.Single(vm.AllBroadcasts).ID);
        }
        else
        {
            Assert.Empty(vm.RecentBrowsed);
            Assert.True(vm.IsLoadInterrupted);
        }
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }

    // PausedUiContext 只支持全量恢复。这里需要确认指定续体执行完毕后再断言，而不是依赖延迟。
    private sealed class LifetimeLoadContext : SynchronizationContext
    {
        private readonly object _gate = new();
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _pending =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();
        private bool _resumed;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            lock (_gate)
            {
                if (!_resumed)
                {
                    _pending.Writer.TryWrite((callback, state));
                    return;
                }
            }
            ThreadPool.QueueUserWorkItem(_ => Run(callback, state));
        }

        public async Task AdvanceOnceAsync()
        {
            var (callback, state) = await _pending.Reader.ReadAsync().AsTask().WaitAsync(LoggingBudget);
            Run(callback, state);
        }

        public async Task AdvanceUntilAsync(Task signal)
        {
            while (!signal.IsCompleted)
            {
                var ready = _pending.Reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(ready, signal).WaitAsync(LoggingBudget) == signal)
                    break;
                await AdvanceOnceAsync();
            }
            await signal.WaitAsync(LoggingBudget);
        }

        public void Resume()
        {
            lock (_gate)
                _resumed = true;
            while (_pending.Reader.TryRead(out var pending))
                Run(pending.Callback, pending.State);
        }

        private void Run(SendOrPostCallback callback, object? state)
        {
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
