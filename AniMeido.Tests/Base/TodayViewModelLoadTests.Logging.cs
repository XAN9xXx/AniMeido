using System.Collections.Concurrent;
using System.Reflection;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    private static readonly TimeSpan LoggingBudget = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Logging_ScheduleFailureWarnsOnceAndKeepsLocalPlans()
    {
        await RunProductionMigrationAsync();
        await LoggingSeedPlanAsync();
        var failure = new HttpRequestException("schedule fixture failure");
        var source = new OfflineSource
        {
            ScheduleRequest = _ => Task.FromException<List<Anime>>(failure),
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (_, _) => Task.FromResult<Anime?>(null),
        };
        var logger = new TodayLog();
        var (vm, reminders) = LoggingCreateViewModel(source, logger);
        using var reminderLifetime = reminders;
        var loading = vm.LoadAsync();
        var theme = vm.Theme.PendingLoad;
        try
        {
            await loading.WaitAsync(LoggingBudget);

            Assert.Equal(80, Assert.Single(vm.Plans).Plan.AnimeId);
            Assert.StartsWith("放送数据加载失败：", vm.ErrorMessage);
            LoggingAssertFailure(logger, failure, LogLevel.Warning);
        }
        finally
        {
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            await Task.WhenAll(loading, theme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_LateScheduleFailureDoesNotLogAfterCancellationOrReplacement(bool replace)
    {
        await RunProductionMigrationAsync();
        var entered = NewCompletion();
        var schedule = NewCompletion<List<Anime>>();
        var failure = new HttpRequestException("obsolete schedule fixture failure");
        var calls = 0;
        var source = new OfflineSource
        {
            ScheduleRequest = _ =>
            {
                if (Interlocked.Increment(ref calls) > 1)
                    return Task.FromResult(new List<Anime> { Broadcast(2) });

                entered.TrySetResult();
                return schedule.Task;
            },
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (_, _) => Task.FromResult<Anime?>(null),
        };
        var logger = new TodayLog();
        var (vm, reminders) = LoggingCreateViewModel(source, logger);
        using var reminderLifetime = reminders;
        using var cancellation = new CancellationTokenSource();
        var loading = vm.LoadAsync(cancellation.Token);
        var firstTheme = vm.Theme.PendingLoad;
        Task replacement = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (replace)
            {
                replacement = vm.LoadAsync();
                await replacement.WaitAsync(LoggingBudget);
            }
            else
            {
                cancellation.Cancel();
            }

            // 请求故意忽略取消，在失去页面所有权后才交回真实网络失败。
            schedule.SetException(failure);
            await loading.WaitAsync(LoggingBudget);

            Assert.DoesNotContain(logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
            Assert.Null(vm.ErrorMessage);
            if (replace)
                Assert.Equal(2, Assert.Single(vm.AllBroadcasts).ID);
            else
                Assert.True(vm.IsLoadInterrupted);
        }
        finally
        {
            cancellation.Cancel();
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            schedule.TrySetResult([]);
            await Task.WhenAll(loading, replacement, firstTheme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_SynchronousSourceFailureErrorsOnlyIfItStillOwnsTheLoad(bool replace)
    {
        await RunProductionMigrationAsync();
        var failure = new HttpRequestException("synchronous schedule fixture failure");
        var source = new OfflineSource
        {
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (_, _) => Task.FromResult<Anime?>(null),
        };
        var logger = new TodayLog();
        var (vm, reminders) = LoggingCreateViewModel(source, logger);
        using var reminderLifetime = reminders;
        var calls = 0;
        Task replacement = Task.CompletedTask;
        Task firstTheme = Task.CompletedTask;
        source.ScheduleRequest = _ =>
        {
            if (++calls > 1)
                return Task.FromResult(new List<Anime> { Broadcast(2) });

            firstTheme = vm.Theme.PendingLoad;
            if (replace)
                replacement = vm.LoadAsync();
            throw failure;
        };
        var loading = vm.LoadAsync();
        try
        {
            await Task.WhenAll(loading, replacement).WaitAsync(LoggingBudget);

            if (replace)
            {
                Assert.DoesNotContain(logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
                Assert.Null(vm.ErrorMessage);
                Assert.Equal(2, Assert.Single(vm.AllBroadcasts).ID);
            }
            else
            {
                LoggingAssertFailure(logger, failure, LogLevel.Error);
                Assert.StartsWith("今天页加载失败：", vm.ErrorMessage);
            }
        }
        finally
        {
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            await Task.WhenAll(loading, replacement, firstTheme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_PlanDetailFailureWarnsOnlyWhileTheRequestIsActive(bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        await LoggingSeedPlanAsync();
        var entered = NewCompletion();
        var detail = NewCompletion<Anime?>();
        var failure = new HttpRequestException("plan cover fixture failure");
        var source = new OfflineSource
        {
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (_, _) =>
            {
                entered.TrySetResult();
                return detail.Task;
            },
        };
        var logger = new TodayLog();
        var (vm, reminders) = LoggingCreateViewModel(source, logger);
        using var reminderLifetime = reminders;
        using var cancellation = new CancellationTokenSource();
        var loading = vm.LoadAsync(cancellation.Token);
        var theme = vm.Theme.PendingLoad;
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            if (cancelBeforeFailure)
                cancellation.Cancel();

            detail.SetException(failure);
            await loading.WaitAsync(LoggingBudget);

            var entry = Assert.Single(vm.Plans);
            Assert.Equal(80, entry.Plan.AnimeId);
            Assert.Null(entry.Anime);
            Assert.Null(vm.ErrorMessage);
            if (cancelBeforeFailure)
                Assert.DoesNotContain(logger.Entries, row => ReferenceEquals(row.Exception, failure));
            else
                LoggingAssertFailure(logger, failure, LogLevel.Warning);
        }
        finally
        {
            cancellation.Cancel();
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            detail.TrySetResult(null);
            await Task.WhenAll(loading, theme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Logging_ObsoleteBrowseDetailFailureDoesNotWarnOrReplaceCurrentHistory()
    {
        await RunProductionMigrationAsync();
        var entered = NewCompletion();
        var detail = NewCompletion<Anime?>();
        var failure = new HttpRequestException("obsolete browse fixture failure");
        var detailCalls = 0;
        var observedToken = CancellationToken.None;
        var source = new OfflineSource
        {
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (id, token) =>
            {
                if (Interlocked.Increment(ref detailCalls) > 1)
                    return Task.FromResult<Anime?>(Broadcast(id));

                observedToken = token;
                entered.TrySetResult();
                return detail.Task;
            },
        };
        var logger = new TodayLog();
        var (vm, reminders) = LoggingCreateViewModel(source, logger);
        using var reminderLifetime = reminders;
        var initialLoad = vm.LoadAsync();
        var initialTheme = vm.Theme.PendingLoad;
        Task oldBrowse = Task.CompletedTask;
        Task replacement = Task.CompletedTask;
        try
        {
            await initialLoad.WaitAsync(LoggingBudget);
            await initialTheme.WaitAsync(LoggingBudget);
            await new BrowseHistoryService(DbFactory).RecordAsync(301, "浏览记录301");

            // 主加载失去版本后可能先返回。直接保留真实子任务，避免漏等晚到的降级处理。
            var versionField = typeof(TodayViewModel).GetField("_loadVersion", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(versionField);
            var version = Assert.IsType<int>(versionField.GetValue(vm));
            var browseMethod = typeof(TodayViewModel).GetMethod("LoadBrowseHistoryAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(browseMethod);
            var loadBrowse = browseMethod.CreateDelegate<Func<IReadOnlySet<int>, int, CancellationToken, Task>>(vm);
            oldBrowse = loadBrowse(new HashSet<int>(), version, CancellationToken.None);
            await entered.Task.WaitAsync(LoggingBudget);

            replacement = vm.LoadAsync();
            await replacement.WaitAsync(LoggingBudget);
            Assert.Equal("作品301", Assert.Single(vm.RecentBrowsed).Anime.Title);
            Assert.False(observedToken.IsCancellationRequested);

            detail.SetException(failure);
            await oldBrowse.WaitAsync(LoggingBudget);

            Assert.DoesNotContain(logger.Entries, entry => ReferenceEquals(entry.Exception, failure));
            Assert.Equal("作品301", Assert.Single(vm.RecentBrowsed).Anime.Title);
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            detail.TrySetResult(null);
            await Task.WhenAll(initialLoad, initialTheme, oldBrowse, replacement,
                vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
        }
    }

    private async Task LoggingSeedPlanAsync()
    {
        await new ActionCenterService(DbFactory).UpsertPlanAsync(80, "本地计划", AnimePlanPriority.Normal, null, 0);
        await new TrackingService(DbFactory).SetStatusAsync(80, AnimeTrackingStatus.PlanToWatch);
    }

    private (TodayViewModel ViewModel, PlanReminderCoordinator Reminders) LoggingCreateViewModel(
        OfflineSource source,
        ILogger logger)
    {
        var actionCenter = new ActionCenterService(DbFactory);
        var reminders = new PlanReminderCoordinator(
            actionCenter, new NoopNotificationService(), new NoopNavigator());
        var vm = new TodayViewModel(source, new TrackingService(DbFactory), _featureState, actionCenter, reminders,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory), logger);
        return (vm, reminders);
    }

    private static void LoggingAssertFailure(TodayLog logger, Exception failure, LogLevel level)
    {
        var entry = Assert.Single(logger.Entries, row => ReferenceEquals(row.Exception, failure));
        Assert.Equal(level, entry.Level);
    }

    private sealed record TodayLogEntry(LogLevel Level, Exception? Exception);

    private sealed class TodayLog : ILogger
    {
        public ConcurrentQueue<TodayLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new TodayLogEntry(level, exception));
        }
    }
}
