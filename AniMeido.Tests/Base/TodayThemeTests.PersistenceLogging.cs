using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class TodayThemeTests
{
    [Fact]
    public async Task PersistenceLogging_UnavailableConfigWarnsForReadsAndErrorsForSaveWithoutHidingTheTheme()
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE config";
            await command.ExecuteNonQueryAsync();
        }
        var logger = new ThemeLog();
        var vm = PersistenceViewModel(logger);

        await vm.LoadForDateAsync(DateWith(TodayThemeKind.LastSeasonTop)).WaitAsync(LoggingBudget);

        Assert.False(vm.IsFailed);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsFallback);
        Assert.Equal(TodayThemeViewModel.BatchSize, vm.Items.Count);
        Assert.Collection(logger.Entries,
            entry => AssertPersistenceLog(entry, LogLevel.Warning),
            entry => AssertPersistenceLog(entry, LogLevel.Warning),
            entry => AssertPersistenceLog(entry, LogLevel.Error));
    }

    [Fact]
    public async Task PersistenceLogging_FailedBatchSaveErrorsOnceAndKeepsTheStoredBatchAndCurrentDisplay()
    {
        await RunProductionMigrationAsync();
        var logger = new ThemeLog();
        var vm = PersistenceViewModel(logger);
        await vm.LoadForDateAsync(DateWith(TodayThemeKind.LastSeasonTop)).WaitAsync(LoggingBudget);
        var stored = Assert.IsType<TodayThemeState>(await _featureState.LoadTodayThemeAsync());
        var firstBatch = vm.Items.Select(item => item.Anime.ID).ToArray();
        Assert.Empty(logger.Entries);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_theme_insert BEFORE INSERT ON config
                WHEN NEW.Key='today_theme'
                BEGIN SELECT RAISE(ABORT, 'synthetic theme save failure'); END;
                CREATE TRIGGER fail_theme_update BEFORE UPDATE ON config
                WHEN NEW.Key='today_theme'
                BEGIN SELECT RAISE(ABORT, 'synthetic theme save failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await vm.NextBatchCommand.ExecuteAsync(null).WaitAsync(LoggingBudget);

        Assert.False(vm.IsFailed);
        Assert.Equal(TodayThemeViewModel.BatchSize, vm.Items.Count);
        Assert.Empty(firstBatch.Intersect(vm.Items.Select(item => item.Anime.ID)));
        var actual = Assert.IsType<TodayThemeState>(await _featureState.LoadTodayThemeAsync());
        Assert.Equal(stored.Date, actual.Date);
        Assert.Equal(stored.Theme, actual.Theme);
        Assert.Equal(stored.AnimeIds, actual.AnimeIds);
        var log = Assert.Single(logger.Entries);
        AssertPersistenceLog(log, LogLevel.Error);
        Assert.Contains("synthetic theme save failure", log.Exception!.Message, StringComparison.Ordinal);
    }

    private TodayThemeViewModel PersistenceViewModel(ILogger logger)
        => new(new ThemeSource(), new TrackingService(DbFactory), _featureState,
            new BrowseHistoryService(DbFactory), new ArchiveService(DbFactory),
            new ActionCenterService(DbFactory), logger);

    private static void AssertPersistenceLog(ThemeLogEntry entry, LogLevel level)
    {
        Assert.Equal(level, entry.Level);
        Assert.IsType<SqliteException>(entry.Exception);
    }
}
