using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

public sealed partial class PastSeasonStatusReloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suspend_ImmediatelyStopsLoadingAndRejectsALateSeasonResult(bool fail)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource
        {
            Load = (_, _, _) =>
            {
                entered.TrySetResult();
                return response.Task;
            },
        }, new TrackingService(DbFactory), logger);
        var loading = vm.LoadPastSeasonAnimeAsync(2025, Season.Spring);
        try
        {
            await entered.Task.WaitAsync(LoggingBudget);
            Assert.True(vm.IsLoading);

            vm.Suspend();

            Assert.False(vm.IsLoading);
            Assert.False(loading.IsCompleted);
            if (fail)
                response.SetException(new HttpRequestException("fixture old suspended season failure"));
            else
                response.SetResult([Item(1)]);
            await loading.WaitAsync(LoggingBudget);

            Assert.False(vm.IsLoading);
            Assert.False(vm.IsError);
            Assert.Null(vm.ErrorMessage);
            Assert.Empty(vm.LoadedAnime);
            Assert.Empty(vm.Entries);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            response.TrySetResult([]);
            await LoggingObserveAsync(loading).WaitAsync(LoggingBudget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Suspend_LateOldSeasonCompletionCannotClearANewRequestsLoadingOrData(bool fail)
    {
        await RunProductionMigrationAsync();
        var oldEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldResponse = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentResponse = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource
        {
            Load = (year, _, _) =>
            {
                if (year == 2025)
                {
                    oldEntered.TrySetResult();
                    return oldResponse.Task;
                }
                currentEntered.TrySetResult();
                return currentResponse.Task;
            },
        }, new TrackingService(DbFactory), logger);
        var old = vm.LoadPastSeasonAnimeAsync(2025, Season.Spring);
        Task current = Task.CompletedTask;
        try
        {
            await oldEntered.Task.WaitAsync(LoggingBudget);
            vm.Suspend();
            Assert.False(vm.IsLoading);
            current = vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
            await currentEntered.Task.WaitAsync(LoggingBudget);
            Assert.True(vm.IsLoading);
            if (fail)
                oldResponse.SetException(new HttpRequestException("fixture old replaced season failure"));
            else
                oldResponse.SetResult([Item(1)]);
            await old.WaitAsync(LoggingBudget);

            Assert.True(vm.IsLoading);
            Assert.False(current.IsCompleted);
            Assert.False(vm.IsError);
            Assert.Null(vm.ErrorMessage);
            Assert.Empty(vm.LoadedAnime);
            Assert.Empty(vm.Entries);
            Assert.Empty(logger.Entries);

            currentResponse.SetResult([Item(2)]);
            await current.WaitAsync(LoggingBudget);
            Assert.False(vm.IsLoading);
            Assert.Equal(new PastSeasonTarget(2026, Season.Spring), vm.Season);
            Assert.Equal(2, Assert.Single(vm.LoadedAnime).ID);
            Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
        }
        finally
        {
            oldResponse.TrySetResult([]);
            currentResponse.TrySetResult([]);
            await LoggingObserveAsync(old, current).WaitAsync(LoggingBudget);
        }
    }

    [Fact]
    public async Task Suspend_PreservesSuccessfulContentsSelectionFiltersAndSort()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, logger);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring).WaitAsync(LoggingBudget);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
        Assert.True(await vm.ReloadStatusesAsync().WaitAsync(LoggingBudget));
        vm.SetQuery("作品");
        vm.SelectFormat(AnimeMediaFormat.Television);
        vm.SetSortKey(PastSeasonSortKey.Title);
        vm.ToggleSortDirection();
        vm.SetHideCompleted(true);
        var anime = vm.LoadedAnime;
        var entries = vm.Entries;
        var chips = vm.FormatChips;
        var summary = vm.SummaryText;
        var ascending = vm.SortAscending;

        vm.Suspend();

        Assert.Same(anime, vm.LoadedAnime);
        Assert.Same(entries, vm.Entries);
        Assert.Same(chips, vm.FormatChips);
        Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
        Assert.Equal("作品", vm.Query);
        Assert.Equal(AnimeMediaFormat.Television, vm.SelectedFormat);
        Assert.Equal(PastSeasonSortKey.Title, vm.SortKey);
        Assert.Equal(ascending, vm.SortAscending);
        Assert.True(vm.HideCompleted);
        Assert.Equal(summary, vm.SummaryText);
        Assert.Equal(new PastSeasonTarget(2026, Season.Spring), vm.Season);
        Assert.True(vm.HasData);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsError);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task Suspend_RejectsARealStatusSnapshotPausedBeforePublication()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, logger);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring).WaitAsync(LoggingBudget);
        vm.SetHideCompleted(true);
        var entries = vm.Entries;
        Assert.Equal(2, entries.Count);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
        var (context, reloading) = await PauseStatusesBeforePublicationAsync(vm);
        try
        {
            vm.Suspend();
            Assert.False(reloading.IsCompleted);
            context.Resume();
            Assert.True(await reloading.WaitAsync(LoggingBudget));

            Assert.Same(entries, vm.Entries);
            Assert.Equal(new[] { 1, 2 }, vm.Entries.Select(entry => entry.Anime.ID));
            Assert.All(vm.Entries, entry => Assert.Equal(AnimeTrackingStatus.None, entry.Status));
            Assert.True(vm.HideCompleted);
            Assert.False(vm.IsLoading);
            Assert.Empty(logger.Entries);
        }
        finally
        {
            context.Resume();
            await LoggingObserveAsync(reloading).WaitAsync(LoggingBudget);
        }
    }
}
