using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class TodayViewModelLoadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowseClear_LateRefreshRespectsSuccessfulClearButKeepsFailedClear(bool failClear)
    {
        await RunProductionMigrationAsync();
        var history = new BrowseHistoryService(DbFactory);
        await history.RecordAsync(301, "初始浏览记录");
        var actionCenter = new ActionCenterService(DbFactory);
        await actionCenter.UpsertPlanAsync(80, "初始计划", AnimePlanPriority.Normal, null, 0);
        var browseEntered = NewCompletion();
        var browseDetail = NewCompletion<Anime?>();
        var refreshedSchedule = NewCompletion<List<Anime>>();
        var holdBrowse = false;
        var source = new OfflineSource
        {
            Schedule = [Broadcast(1)],
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (id, _) =>
            {
                if (id != 301 || !holdBrowse)
                    return Task.FromResult<Anime?>(Broadcast(id));

                browseEntered.TrySetResult();
                return browseDetail.Task;
            },
        };
        var (vm, reminders) = LoggingCreateViewModel(source, NullLogger.Instance);
        using var reminderLifetime = reminders;
        var initialLoad = vm.LoadAsync();
        var initialTheme = vm.Theme.PendingLoad;
        Task refreshing = Task.CompletedTask;
        Task refreshedTheme = Task.CompletedTask;
        try
        {
            await Task.WhenAll(initialLoad, initialTheme).WaitAsync(LoggingBudget);
            Assert.Equal(301, Assert.Single(vm.RecentBrowsed).Anime.ID);
            Assert.Equal(1, Assert.Single(vm.AllBroadcasts).ID);
            await actionCenter.UpsertPlanAsync(80, "刷新后的计划", AnimePlanPriority.Normal, null, 0);
            if (failClear)
            {
                using var connection = await DbFactory.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TRIGGER reject_browse_clear BEFORE DELETE ON browse_history
                    BEGIN SELECT RAISE(ABORT, 'fixture browse clear failure'); END;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            holdBrowse = true;
            source.ScheduleRequest = _ => refreshedSchedule.Task;
            refreshing = vm.LoadAsync();
            refreshedTheme = vm.Theme.PendingLoad;
            await browseEntered.Task.WaitAsync(LoggingBudget);
            Assert.True(vm.IsLoading);
            Assert.False(refreshedSchedule.Task.IsCompleted);

            if (failClear)
            {
                var failure = await Assert.ThrowsAsync<SqliteException>(() => vm.ClearBrowseHistoryAsync());
                Assert.Contains("fixture browse clear failure", failure.Message);
                Assert.Equal(301, Assert.Single(await history.GetHistoryAsync()).AnimeId);
                Assert.Equal(301, Assert.Single(vm.RecentBrowsed).Anime.ID);
            }
            else
            {
                await vm.ClearBrowseHistoryAsync();
                Assert.Empty(await history.GetHistoryAsync());
                Assert.Empty(vm.RecentBrowsed);
            }

            // 清空只影响旧浏览结果；仍在等待的本次放送与计划加载应完整发布。
            refreshedSchedule.SetResult([Broadcast(2)]);
            browseDetail.SetResult(Broadcast(301) with { Title = "刷新后的浏览资料" });
            await refreshing.WaitAsync(LoggingBudget);

            if (failClear)
                Assert.Equal("刷新后的浏览资料", Assert.Single(vm.RecentBrowsed).Anime.Title);
            else
                Assert.Empty(vm.RecentBrowsed);
            Assert.Equal(2, Assert.Single(vm.AllBroadcasts).ID);
            Assert.Equal("刷新后的计划", Assert.Single(vm.Plans).Plan.TitleSnapshot);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsLoadInterrupted);
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            browseDetail.TrySetResult(Broadcast(301));
            refreshedSchedule.TrySetResult([]);
            try
            {
                await Task.WhenAll(initialLoad, refreshing).WaitAsync(LoggingBudget);
            }
            finally
            {
                vm.SuspendPlanDetails();
                vm.Theme.CancelLoad();
                await Task.WhenAll(initialTheme, refreshedTheme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
            }
        }
    }

    [Fact]
    public async Task BrowseClear_NewHistoryAppearsOnTheNextLoad()
    {
        await RunProductionMigrationAsync();
        var history = new BrowseHistoryService(DbFactory);
        await history.RecordAsync(301, "清空前的记录");
        var source = new OfflineSource
        {
            Schedule = [Broadcast(1)],
            SeasonRequest = (_, _, _) => Task.FromResult(new List<Anime>()),
            DetailRequest = (id, _) => Task.FromResult<Anime?>(Broadcast(id)),
        };
        var (vm, reminders) = LoggingCreateViewModel(source, NullLogger.Instance);
        using var reminderLifetime = reminders;
        var initialLoad = vm.LoadAsync();
        var initialTheme = vm.Theme.PendingLoad;
        Task nextLoad = Task.CompletedTask;
        Task nextTheme = Task.CompletedTask;
        try
        {
            await Task.WhenAll(initialLoad, initialTheme).WaitAsync(LoggingBudget);
            Assert.Equal(301, Assert.Single(vm.RecentBrowsed).Anime.ID);

            await vm.ClearBrowseHistoryAsync();
            Assert.Empty(await history.GetHistoryAsync());
            Assert.Empty(vm.RecentBrowsed);
            await history.RecordAsync(302, "清空后的新记录");
            nextLoad = vm.LoadAsync();
            nextTheme = vm.Theme.PendingLoad;
            await nextLoad.WaitAsync(LoggingBudget);

            Assert.Equal(302, Assert.Single(await history.GetHistoryAsync()).AnimeId);
            Assert.Equal(302, Assert.Single(vm.RecentBrowsed).Anime.ID);
            Assert.Equal(1, Assert.Single(vm.AllBroadcasts).ID);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsLoadInterrupted);
            Assert.Null(vm.ErrorMessage);
        }
        finally
        {
            try
            {
                await Task.WhenAll(initialLoad, nextLoad).WaitAsync(LoggingBudget);
            }
            finally
            {
                vm.SuspendPlanDetails();
                vm.Theme.CancelLoad();
                await Task.WhenAll(initialTheme, nextTheme, vm.Theme.PendingLoad).WaitAsync(LoggingBudget);
            }
        }
    }
}
