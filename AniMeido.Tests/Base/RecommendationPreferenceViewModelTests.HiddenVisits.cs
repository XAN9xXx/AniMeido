using System.Collections.ObjectModel;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public partial class RecommendationPreferenceViewModelTests
{
    [Theory]
    [InlineData("restore", false)]
    [InlineData("restore", true)]
    [InlineData("clear", false)]
    [InlineData("clear", true)]
    public async Task HiddenSave_CommittedPreviousVisitCannotChangeCurrentListOrMessage(string operation, bool cancel)
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        await service.HideAnimeAsync(1, "作品1");
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        await vm.LoadAsync().WaitAsync(ActionBudget);
        var original = Assert.Single(vm.HiddenAnime);
        using var previousVisit = new CancellationTokenSource();
        var context = new PausedUiContext();
        using var blocker = await LockHiddenVisitDatabaseAsync();
        var saving = BeginPausedHiddenVisitAsync(() => operation == "restore"
            ? vm.RestoreAsync(original, previousVisit.Token)
            : vm.ClearHiddenAsync(previousVisit.Token), context);
        try
        {
            Assert.False(saving.IsCompleted);
            blocker.Dispose();
            await context.FirstPost.WaitAsync(ActionBudget);
            Assert.False(saving.IsCompleted);
            Assert.Empty(await service.GetHiddenAnimeAsync());

            await service.HideAnimeAsync(2, "作品2");
            var current = new ObservableCollection<RecommendationHiddenAnime>(await service.GetHiddenAnimeAsync());
            vm.HiddenAnime = current;
            vm.Suspend();
            if (cancel)
                previousVisit.Cancel();
            vm.ReportError("保留当前访问中的提示。");
            context.Resume();
            await saving.WaitAsync(ActionBudget);

            Assert.Same(current, vm.HiddenAnime);
            Assert.Equal(2, Assert.Single(vm.HiddenAnime).AnimeId);
            Assert.Equal("保留当前访问中的提示。", vm.Message);
            Assert.True(vm.HasError);
            Assert.Equal(2, Assert.Single(await service.GetHiddenAnimeAsync()).AnimeId);
            Assert.Empty(logger.Entries);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            await ObserveHiddenVisitAsync(saving).WaitAsync(ActionBudget);
        }
    }

    [Theory]
    [InlineData("restore")]
    [InlineData("clear")]
    public async Task HiddenSave_CurrentVisitPublishesTheCommittedResult(string operation)
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        await service.HideAnimeAsync(1, "作品1");
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        await vm.LoadAsync().WaitAsync(ActionBudget);
        var original = Assert.Single(vm.HiddenAnime);

        await (operation == "restore"
            ? vm.RestoreAsync(original, CancellationToken.None)
            : vm.ClearHiddenAsync(CancellationToken.None)).WaitAsync(ActionBudget);

        Assert.Empty(await service.GetHiddenAnimeAsync());
        Assert.Empty(vm.HiddenAnime);
        Assert.False(vm.HasHiddenAnime);
        Assert.Equal(operation == "restore" ? "已恢复。刷新后该作品可能重新出现。" : "已恢复全部隐藏作品。", vm.Message);
        Assert.False(vm.HasError);
        Assert.Empty(logger.Entries);
        Assert.Equal(0, source.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenLoad_CachedPreviousVisitCannotPublishAnOldListAfterSuspend(bool cancel)
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        await service.HideAnimeAsync(1, "作品1");
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        using var previousVisit = new CancellationTokenSource();
        var context = new PausedUiContext();
        using var blocker = await LockHiddenVisitDatabaseAsync();
        var loading = BeginPausedHiddenVisitAsync(() => vm.LoadAsync(previousVisit.Token), context);
        try
        {
            Assert.False(loading.IsCompleted);
            blocker.Dispose();
            await context.FirstPost.WaitAsync(ActionBudget);
            Assert.False(loading.IsCompleted);
            Assert.Empty(vm.HiddenAnime);
            Assert.Equal(1, Assert.Single(await service.GetHiddenAnimeAsync()).AnimeId);

            await service.RestoreAnimeAsync(1);
            await service.HideAnimeAsync(2, "作品2");
            var current = new ObservableCollection<RecommendationHiddenAnime>(await service.GetHiddenAnimeAsync());
            vm.HiddenAnime = current;
            vm.Suspend();
            if (cancel)
                previousVisit.Cancel();
            vm.ReportError("保留当前访问中的提示。");
            context.Resume();
            await loading.WaitAsync(ActionBudget);

            Assert.Same(current, vm.HiddenAnime);
            Assert.Equal(2, Assert.Single(vm.HiddenAnime).AnimeId);
            Assert.Equal("保留当前访问中的提示。", vm.Message);
            Assert.True(vm.HasError);
            Assert.False(vm.IsBusy);
            Assert.Equal(2, Assert.Single(await service.GetHiddenAnimeAsync()).AnimeId);
            Assert.Empty(logger.Entries);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            await ObserveHiddenVisitAsync(loading).WaitAsync(ActionBudget);
        }
    }

    private static Task BeginPausedHiddenVisitAsync(Func<Task> action, PausedUiContext context)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            return action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private async Task<SqliteConnection> LockHiddenVisitDatabaseAsync()
    {
        using (var pooled = DbFactory.CreateConnection())
            SqliteConnection.ClearPool(pooled);
        var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString());
        try
        {
            await blocker.OpenAsync();
            using var command = blocker.CreateCommand();
            command.CommandTimeout = 2;
            command.CommandText = "PRAGMA journal_mode=DELETE";
            Assert.Equal("delete", await command.ExecuteScalarAsync());
            command.CommandText = "BEGIN EXCLUSIVE";
            await command.ExecuteNonQueryAsync();
            return blocker;
        }
        catch
        {
            blocker.Dispose();
            throw;
        }
    }

    private static async Task ObserveHiddenVisitAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
