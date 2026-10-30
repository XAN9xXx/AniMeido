using AniMeido.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed partial class PastSeasonStatusReloadTests : DbTestBase
{
    [Theory]
    [InlineData(AnimeTrackingStatus.Blocked, false)]
    [InlineData(AnimeTrackingStatus.Completed, false)]
    [InlineData(AnimeTrackingStatus.Completed, true)]
    public async Task ReloadStatuses_AppliesCurrentMarksAndFiltersWithoutReplacingTheList(
        AnimeTrackingStatus status,
        bool hideCompleted)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, NullLogger.Instance);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
        vm.SetHideCompleted(hideCompleted);
        var entries = vm.Entries;

        await tracking.SetStatusAsync(1, status);
        Assert.True(await vm.ReloadStatusesAsync());

        Assert.Same(entries, vm.Entries);
        AssertCurrentSnapshot(vm, status, hideCompleted);
    }

    [Theory]
    [InlineData(AnimeTrackingStatus.Blocked, false)]
    [InlineData(AnimeTrackingStatus.Completed, false)]
    [InlineData(AnimeTrackingStatus.Completed, true)]
    [Trait("Category", "Mechanism")]
    public async Task ReloadStatuses_OldSnapshotCannotUndoNewMarksOrFilters(
        AnimeTrackingStatus status,
        bool hideCompleted)
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, NullLogger.Instance);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
        vm.SetHideCompleted(hideCompleted);
        var entries = vm.Entries;
        var (context, oldReload) = await PauseStatusesBeforePublicationAsync(vm);
        Task<bool>? currentReload = null;

        try
        {
            await tracking.SetStatusAsync(1, status);
            currentReload = vm.ReloadStatusesAsync();
            Assert.True(await currentReload.WaitAsync(TimeSpan.FromSeconds(5)));
            AssertCurrentSnapshot(vm, status, hideCompleted);
        }
        finally
        {
            context.Resume();
            await Task.WhenAll(oldReload, currentReload ?? Task.FromResult(true))
                .WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(await oldReload);
        Assert.Same(entries, vm.Entries);
        AssertCurrentSnapshot(vm, status, hideCompleted);
    }

    [Fact]
    public async Task ReloadStatuses_CurrentSqlFailureReturnsFalseAndKeepsPreviousMarks()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, NullLogger.Instance);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
        Assert.True(await vm.ReloadStatusesAsync());
        var entries = vm.Entries;
        var completed = vm.Entries.Single(entry => entry.Anime.ID == 1);

        using (var connection = await DbFactory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE tracking";
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await vm.ReloadStatusesAsync().WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(entries, vm.Entries);
        Assert.Same(completed, vm.Entries.Single(entry => entry.Anime.ID == 1));
        AssertCurrentSnapshot(vm, AnimeTrackingStatus.Completed, hideCompleted: false);
        vm.SetHideCompleted(true);
        AssertCurrentSnapshot(vm, AnimeTrackingStatus.Completed, hideCompleted: true);
    }

    private static void AssertCurrentSnapshot(
        PastSeasonViewModel vm,
        AnimeTrackingStatus status,
        bool hideCompleted)
    {
        Assert.Equal(hideCompleted, vm.HideCompleted);
        if (status == AnimeTrackingStatus.Blocked || hideCompleted)
        {
            Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
        }
        else
        {
            Assert.Equal(new[] { 1, 2 }, vm.Entries.Select(entry => entry.Anime.ID));
            Assert.Equal(status, vm.Entries.Single(entry => entry.Anime.ID == 1).Status);
        }

        Assert.Equal(AnimeTrackingStatus.None, vm.Entries.Single(entry => entry.Anime.ID == 2).Status);
        Assert.Equal(vm.Entries.Count, vm.FormatChips.Single(chip => chip.Format is null).Count);
    }

    private async Task<(PausedUiContext Context, Task<bool> Reloading)> PauseStatusesBeforePublicationAsync(
        PastSeasonViewModel vm)
    {
        var context = new PausedUiContext();
        using var blocker = await LockStatusDatabaseAsync();
        Task<bool>? reloading = null;

        try
        {
            var previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                reloading = vm.ReloadStatusesAsync();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
            Assert.False(reloading.IsCompleted);

            blocker.Dispose();
            await context.FirstPost.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reloading.IsCompleted);
            return (context, reloading);
        }
        catch
        {
            blocker.Dispose();
            context.Resume();
            if (reloading is not null)
                await reloading.WaitAsync(TimeSpan.FromSeconds(5));
            throw;
        }
    }

    private async Task<SqliteConnection> LockStatusDatabaseAsync()
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

    private static Anime Item(int id) => new(
        id, $"作品{id}", null, [], new DateOnly(2026, 4, 1), null, "", 2026, 4,
        Score: 9 - id, MediaFormat: AnimeMediaFormat.Television);

    private sealed class SeasonSource : IAnimeDataSource
    {
        public Func<int, Season, CancellationToken, Task<List<Anime>>> Load { get; init; }
            = (_, _, _) => Task.FromResult(new List<Anime> { Item(1), Item(2) });

        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct)
            => Load(year, season, ct);

        private static T Unexpected<T>() => throw new NotSupportedException("Unexpected data-source request.");

        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => Unexpected<Task<List<Anime>>>();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Unexpected<Task<Anime?>>();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Studio>>>();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<Tag>>>();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<VoiceActor>>>();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Unexpected<Task<List<CharacterRole>>>();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => Unexpected<Task<List<PersonWork>>>();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct,
            string? airDateFrom = null, string? airDateTo = null) => Unexpected<Task<(List<Anime>, int)>>();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct)
            => Unexpected<Task<(List<Anime>, int, int)>>();
    }
}
