using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class ManagementViewModelTests
{
    [Theory]
    [InlineData(false, false, "stay")]
    [InlineData(false, true, "stay")]
    [InlineData(true, false, "stay")]
    [InlineData(true, true, "stay")]
    [InlineData(false, false, "leave")]
    [InlineData(true, true, "leave")]
    [InlineData(false, false, "reload")]
    [InlineData(true, true, "reload")]
    public async Task StatusChangesWaitForEarlierWritesAndKeepTheLastSelection(
        bool firstIsBatch, bool lastIsBatch, string navigation)
    {
        var vm = await CreateViewModelAsync(new ManagementSource());
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Watching);
        await vm.LoadAsync();
        var first = vm.Entries.Single(entry => entry.Anime.ID == 1);
        var second = vm.Entries.Single(entry => entry.Anime.ID == 2);
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var writeLock = connection.BeginTransaction();
        var earlier = vm.ChangeStatusAsync(
            firstIsBatch ? [first, second] : [first], AnimeTrackingStatus.PlanToWatch);
        Task<StatusChangeResult>? later = null;
        Task? loading = null;
        try
        {
            Assert.False(earlier.IsCompleted);
            if (navigation == "leave")
                vm.CancelPendingLoads();
            else if (navigation == "reload")
                loading = vm.LoadAsync();

            later = vm.ChangeStatusAsync(
                lastIsBatch ? [first, second] : [first], AnimeTrackingStatus.Watching);
            Assert.False(later.IsCompleted);

            writeLock.Rollback();
            Assert.Equal(new StatusChangeResult(firstIsBatch ? 2 : 1, 0), await earlier.WaitAsync(Budget));
            Assert.Equal(new StatusChangeResult(firstIsBatch && lastIsBatch ? 2 : 1, 0), await later.WaitAsync(Budget));
            if (loading is not null)
                await loading.WaitAsync(Budget);
            Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(1));
            Assert.Equal(firstIsBatch && !lastIsBatch ? AnimeTrackingStatus.PlanToWatch : AnimeTrackingStatus.Watching,
                await tracking.GetStatusAsync(2));
        }
        finally
        {
            writeLock.Dispose();
            await earlier.WaitAsync(Budget);
            if (later is not null)
                await later.WaitAsync(Budget);
            if (loading is not null)
                await loading.WaitAsync(Budget);
            vm.CancelPendingLoads();
        }
    }

    [Fact]
    public async Task SelectingTheAlreadyCommittedStatusKeepsItsTimestamp()
    {
        var vm = await CreateViewModelAsync(new ManagementSource());
        var tracking = new TrackingService(DbFactory);
        var before = await tracking.GetAllTrackingAsync();
        try
        {
            var result = await vm.ChangeStatusAsync([Assert.Single(vm.Entries)], AnimeTrackingStatus.Watching);

            Assert.Equal(new StatusChangeResult(0, 0), result);
            Assert.Equal(before, await tracking.GetAllTrackingAsync());
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }

    [Fact]
    public async Task AFailedStatusChangeStillAllowsTheNextQueuedChange()
    {
        var vm = await CreateViewModelAsync(new ManagementSource());
        var entry = Assert.Single(vm.Entries);
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER reject_plan BEFORE INSERT ON tracking
            WHEN NEW.Status = {(int)AnimeTrackingStatus.PlanToWatch}
            BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;
            """;
        await command.ExecuteNonQueryAsync();
        try
        {
            var failing = vm.ChangeStatusAsync([entry], AnimeTrackingStatus.PlanToWatch);
            var following = vm.ChangeStatusAsync([entry], AnimeTrackingStatus.Completed);

            Assert.Equal(new StatusChangeResult(0, 1), await failing.WaitAsync(Budget));
            Assert.Equal(new StatusChangeResult(1, 0), await following.WaitAsync(Budget));
            Assert.Equal(AnimeTrackingStatus.Completed, await new TrackingService(DbFactory).GetStatusAsync(1));
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }
}
