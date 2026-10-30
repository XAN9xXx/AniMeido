using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    [Fact]
    public async Task Visibility_PreCancellationKeepsAllPublishedContent()
    {
        await RunProductionMigrationAsync();
        await new TrackingService(DbFactory).SetStatusAsync(1, AnimeTrackingStatus.Blocked);
        var (vm, reminders) = LoggingCreateViewModel(new OfflineSource(), NullLogger.Instance);
        using var reminderLifetime = reminders;
        SeedVisibilityContent(vm, 2);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => vm.RemoveBlockedEntriesAsync(cancellation.Token));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            AssertVisibilityContent(vm, [1, 2]);
        }
        finally
        {
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            await vm.Theme.PendingLoad.WaitAsync(LoggingBudget);
        }
    }

    [Fact]
    public async Task Visibility_CancellationAfterSqlReadCannotChangeTheNextVisitContent()
    {
        await RunProductionMigrationAsync();
        await new TrackingService(DbFactory).SetStatusAsync(1, AnimeTrackingStatus.Blocked);
        var (vm, reminders) = LoggingCreateViewModel(new OfflineSource(), NullLogger.Instance);
        using var reminderLifetime = reminders;
        SeedVisibilityContent(vm, 2);
        using var blocker = await LockVisibilityDatabaseAsync();
        using var cancellation = new CancellationTokenSource();
        var context = new PausedUiContext();
        Task refreshing;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            refreshing = vm.RemoveBlockedEntriesAsync(cancellation.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        try
        {
            Assert.False(refreshing.IsCompleted);
            blocker.Dispose();
            // Tracking的SQL已经完成；这里只扣住尚未发布可见内容的VM续体。
            await context.FirstPost.WaitAsync(LoggingBudget);
            Assert.False(refreshing.IsCompleted);
            SeedVisibilityContent(vm, 3);
            var newBroadcasts = vm.AllBroadcasts;
            var newPlans = vm.Plans;
            cancellation.Cancel();
            context.Resume();

            var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => refreshing.WaitAsync(LoggingBudget));

            Assert.Equal(cancellation.Token, failure.CancellationToken);
            Assert.Same(newBroadcasts, vm.AllBroadcasts);
            Assert.Same(newPlans, vm.Plans);
            AssertVisibilityContent(vm, [1, 3]);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            try
            {
                // 超时预算先独立检查，不能被后续的原异常观察吞掉。
                await Task.WhenAny(refreshing).WaitAsync(LoggingBudget);
                await refreshing.ConfigureAwait(
                    ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            }
            finally
            {
                vm.SuspendPlanDetails();
                vm.Theme.CancelLoad();
                await vm.Theme.PendingLoad.WaitAsync(LoggingBudget);
            }
        }
    }

    [Fact]
    public async Task Visibility_CurrentVisitFiltersBlockedItemsAndKeepsOtherItems()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Blocked);
        var (vm, reminders) = LoggingCreateViewModel(new OfflineSource(), NullLogger.Instance);
        using var reminderLifetime = reminders;
        SeedVisibilityContent(vm, 2);
        try
        {
            await vm.RemoveBlockedEntriesAsync(CancellationToken.None).WaitAsync(LoggingBudget);

            AssertVisibilityContent(vm, [2]);
            Assert.Equal(AnimeTrackingStatus.Blocked, await tracking.GetStatusAsync(1));
        }
        finally
        {
            vm.SuspendPlanDetails();
            vm.Theme.CancelLoad();
            await vm.Theme.PendingLoad.WaitAsync(LoggingBudget);
        }
    }

    private async Task<SqliteConnection> LockVisibilityDatabaseAsync()
    {
        using (var pooled = DbFactory.CreateConnection())
            SqliteConnection.ClearPool(pooled);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandTimeout = 2;
            // WAL读者可绕过写锁；独立测试库改为DELETE以阻止SQL提前完成。
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void SeedVisibilityContent(TodayViewModel vm, int otherId)
    {
        var items = new[] { Broadcast(1), Broadcast(otherId) };
        vm.AllBroadcasts = new(items);
        vm.PersonalBroadcasts = new(items.Select(item => new TodayAnimeEntry(item, "放送说明")));
        vm.ContinueWatching = new(items.Select(item => new TodayAnimeEntry(item, "尚未开始")));
        vm.RecentActivity = new(items.Select(item => new TodayAnimeEntry(item, "最近观看")));
        vm.RecentBrowsed = new(items.Select(item => new TodayBrowseEntry(item, "最近浏览")));
        var at = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        vm.Plans = new(items.Select((item, index) => new TodayPlanEntry(
            new AnimePlan(item.ID, item.Title, AnimePlanPriority.Normal, null, index, at, at, null, null),
            "计划说明")));
    }

    private static void AssertVisibilityContent(TodayViewModel vm, int[] expected)
    {
        Assert.Equal(expected, vm.AllBroadcasts.Select(item => item.ID));
        Assert.Equal(expected, vm.PersonalBroadcasts.Select(item => item.Anime.ID));
        Assert.Equal(expected, vm.ContinueWatching.Select(item => item.Anime.ID));
        Assert.Equal(expected, vm.RecentActivity.Select(item => item.Anime.ID));
        Assert.Equal(expected, vm.RecentBrowsed.Select(item => item.Anime.ID));
        Assert.Equal(expected, vm.Plans.Select(item => item.Plan.AnimeId));
    }
}
