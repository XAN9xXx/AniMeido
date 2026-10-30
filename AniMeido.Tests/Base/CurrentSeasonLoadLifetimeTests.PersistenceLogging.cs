using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class CurrentSeasonLoadLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_TrackingReadFailureUsesFallbackAndWarnsOnce(bool reloadExisting)
    {
        await RunProductionMigrationAsync();
        var date = new DateOnly(2026, 9, 18);
        var anime = CalendarPersistenceAnime(101, date);
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(anime.ID, AnimeTrackingStatus.Watching);
        var source = CalendarPersistenceSource(date, [anime],
            (_, _, _) => Task.FromResult(reloadExisting ? new List<Anime>() : [anime]));
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(date), logger);
        vm.SetDiscoverCapacity(1);
        Task loading = Task.CompletedTask;
        try
        {
            if (reloadExisting)
            {
                loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
                await loading.WaitAsync(Budget);
                await AwaitCalendarPersistenceWorkAsync(vm);
                Assert.Equal(AnimeTrackingStatus.Watching, Assert.Single(vm.VisibleEntries).Status);
                Assert.Empty(logger.Entries);
            }

            await ExecuteCalendarFixtureSqlAsync("ALTER TABLE tracking RENAME TO fixture_tracking");
            loading = reloadExisting ? vm.ReloadStatusesAsync() : vm.LoadTimeMachineAsync();
            await loading.WaitAsync(Budget);
            await AwaitCalendarPersistenceWorkAsync(vm);

            if (reloadExisting)
            {
                Assert.True(vm.HasData);
                Assert.Equal(AnimeTrackingStatus.Watching, Assert.Single(vm.VisibleEntries).Status);
            }
            else
            {
                Assert.Equal(AnimeTrackingStatus.None, Assert.Single(vm.TimeMachinePicks).Status);
                Assert.False(vm.IsTimeMachineFailed);
            }
            Assert.False(vm.IsError);
            AssertCalendarPersistenceLog(logger, LogLevel.Warning, 1, "no such table: tracking", nameof(TrackingService));
        }
        finally
        {
            await StopCalendarPersistenceAsync(vm, loading);
        }
    }

    [Fact]
    public async Task Logging_DailyPickReadFailureChoosesAndSavesForThisRunAndWarnsOnce()
    {
        await RunProductionMigrationAsync();
        var date = new DateOnly(2026, 9, 18);
        var schedule = new[] { CalendarPersistenceAnime(101, date), CalendarPersistenceAnime(102, date) };
        var order = CurrentSeasonViewModel.BuildDailyOrder(schedule.Select(anime => anime.ID), date);
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveCalendarDailyPickAsync(date, order[1]);
        await FailCalendarConfigReadAsync("calendar_daily_pick");
        var currentSeason = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = CalendarPersistenceSource(date, schedule,
            (year, _, _) => year == date.Year ? currentSeason.Task : Task.FromResult(new List<Anime>()));
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(date), logger);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        try
        {
            await loading.WaitAsync(Budget);
            await (vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
            Assert.True(vm.HasData);
            Assert.False(vm.IsError);
            Assert.Equal(order[0], vm.DailyPick?.Anime.ID);
            AssertCalendarPersistenceLog(logger, LogLevel.Warning, 1, "integer overflow", nameof(PersonalFeatureStateStore));

            await RestoreCalendarConfigAsync();
            Assert.Equal((date, order[0]), await _featureState.LoadCalendarDailyPickAsync());
        }
        finally
        {
            vm.CancelSupplementaryLoads();
            currentSeason.TrySetResult([]);
            await StopCalendarPersistenceAsync(vm, loading);
        }
    }

    [Fact]
    public async Task Logging_DailyPickSaveFailureKeepsNewDisplayAndOldStoredChoiceAndErrorsOnce()
    {
        await RunProductionMigrationAsync();
        var date = new DateOnly(2026, 9, 18);
        var schedule = new[] { CalendarPersistenceAnime(101, date), CalendarPersistenceAnime(102, date) };
        var tracking = new TrackingService(DbFactory);
        var source = CalendarPersistenceSource(date, schedule);
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(date), logger);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        try
        {
            await loading.WaitAsync(Budget);
            await AwaitCalendarPersistenceWorkAsync(vm);
            var original = vm.DailyPick!.Anime.ID;
            Assert.Equal((date, original), await _featureState.LoadCalendarDailyPickAsync());
            Assert.Empty(logger.Entries);
            await RejectCalendarConfigWriteAsync("calendar_daily_pick");

            await vm.NextDailyPickCommand.ExecuteAsync(null).WaitAsync(Budget);
            await (vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);

            Assert.True(vm.HasData);
            Assert.False(vm.IsError);
            Assert.NotEqual(original, vm.DailyPick!.Anime.ID);
            Assert.Equal((date, original), await _featureState.LoadCalendarDailyPickAsync());
            AssertCalendarPersistenceLog(logger, LogLevel.Error, 19, "fixture rejected calendar_daily_pick", nameof(PersonalFeatureStateStore));
        }
        finally
        {
            await StopCalendarPersistenceAsync(vm, loading);
        }
    }

    [Fact]
    public async Task Logging_TimeMachineShownReadFailureStillDisplaysCandidatesAndWarnsOnce()
    {
        await RunProductionMigrationAsync();
        var date = new DateOnly(2026, 9, 18);
        var anime = CalendarPersistenceAnime(101, date);
        var target = (Year: date.Year - 10, Season: SeasonHelper.FromMonth(date.Month));
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveTimeMachineShownAsync(
            new Dictionary<(int Year, Season Season), IReadOnlyList<int>> { [target] = [anime.ID] });
        await FailCalendarConfigReadAsync("time_machine_shown");
        var source = CalendarPersistenceSource(date, [], (_, _, _) => Task.FromResult(new List<Anime> { anime }));
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(date), logger);
        var loading = vm.LoadTimeMachineAsync();
        try
        {
            await loading.WaitAsync(Budget);
            Assert.False(vm.IsTimeMachineFailed);
            Assert.False(vm.IsTimeMachineLoading);
            Assert.False(vm.IsError);
            AssertCalendarPersistenceLog(logger, LogLevel.Warning, 1, "integer overflow", nameof(PersonalFeatureStateStore));

            // 容量为零时只读取；恢复存储后再显示，避免把另一次保存失败混进读取断言。
            await RestoreCalendarConfigAsync();
            vm.SetDiscoverCapacity(1);
            await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
            Assert.Equal(anime.ID, Assert.Single(vm.TimeMachinePicks).Anime.ID);
            Assert.Single(logger.Entries);
        }
        finally
        {
            await StopCalendarPersistenceAsync(vm, loading);
        }
    }

    [Fact]
    public async Task Logging_TimeMachineShownSaveFailureKeepsDisplayAndDoesNotRetryInTheSamePage()
    {
        await RunProductionMigrationAsync();
        var date = new DateOnly(2026, 9, 18);
        var anime = CalendarPersistenceAnime(101, date);
        var target = (Year: date.Year - 10, Season: SeasonHelper.FromMonth(date.Month));
        var tracking = new TrackingService(DbFactory);
        await _featureState.SaveTimeMachineShownAsync(
            new Dictionary<(int Year, Season Season), IReadOnlyList<int>> { [target] = [999] });
        var source = CalendarPersistenceSource(date, [], (_, _, _) => Task.FromResult(new List<Anime> { anime }));
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, tracking, _featureState, new CalendarTestTime(date), logger);
        var loading = vm.LoadTimeMachineAsync();
        try
        {
            await loading.WaitAsync(Budget);
            Assert.Empty(logger.Entries);
            await RejectCalendarConfigWriteAsync("time_machine_shown");

            vm.SetDiscoverCapacity(1);
            await vm.PendingTimeMachineShownSave.WaitAsync(Budget);

            Assert.Equal(anime.ID, Assert.Single(vm.TimeMachinePicks).Anime.ID);
            Assert.False(vm.IsTimeMachineFailed);
            Assert.False(vm.IsError);
            Assert.Equal(new[] { 999 }, (await _featureState.LoadTimeMachineShownAsync())[target]);
            AssertCalendarPersistenceLog(logger, LogLevel.Error, 19, "fixture rejected time_machine_shown", nameof(PersonalFeatureStateStore));

            var failedSave = vm.PendingTimeMachineShownSave;
            await ExecuteCalendarFixtureSqlAsync("DROP TRIGGER fixture_reject_config");
            vm.SetDiscoverCapacity(0);
            vm.SetDiscoverCapacity(1);
            await vm.PendingTimeMachineShownSave.WaitAsync(Budget);

            Assert.Equal(anime.ID, Assert.Single(vm.TimeMachinePicks).Anime.ID);
            Assert.Same(failedSave, vm.PendingTimeMachineShownSave);
            Assert.Equal(new[] { 999 }, (await _featureState.LoadTimeMachineShownAsync())[target]);
            Assert.Single(logger.Entries);
        }
        finally
        {
            await StopCalendarPersistenceAsync(vm, loading);
        }
    }

    private static Anime CalendarPersistenceAnime(int id, DateOnly date)
        => new(id, $"fixture {id}", null, [], new DateOnly(date.Year, 7, 3), null, "", date.Year, 7,
            AnimeListPresentation.ToBangumiWeekday(date.DayOfWeek), 7, MediaFormat: AnimeMediaFormat.Television);

    private static PendingScheduleSource CalendarPersistenceSource(
        DateOnly date,
        IEnumerable<Anime> schedule,
        Func<int, Season, CancellationToken, Task<List<Anime>>>? seasonRequest = null)
    {
        var source = new PendingScheduleSource
        {
            SeasonRequest = seasonRequest ?? ((_, _, _) => Task.FromResult(new List<Anime>())),
            DetailRequest = (id, _) => Task.FromResult<Anime?>(CalendarPersistenceAnime(id, date)),
            TagsRequest = (_, _) => Task.FromResult(new List<Tag>()),
        };
        source.Schedule.SetResult(schedule.ToList());
        return source;
    }

    private static void AssertCalendarPersistenceLog(
        CalendarLog logger, LogLevel level, int errorCode, string message, string sourceType)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        var error = Assert.IsType<SqliteException>(entry.Exception);
        Assert.Equal(errorCode, error.SqliteErrorCode);
        Assert.Contains(message, error.Message);
        Assert.Contains(sourceType, error.StackTrace);
    }

    private Task FailCalendarConfigReadAsync(string key)
        => ExecuteCalendarFixtureSqlAsync($"""
            ALTER TABLE config RENAME TO fixture_config;
            CREATE VIEW config AS
                SELECT Key, CASE WHEN Key='{key}' THEN abs(-9223372036854775808) ELSE Value END AS Value
                FROM fixture_config;
            CREATE TRIGGER fixture_config_insert INSTEAD OF INSERT ON config
            BEGIN
                INSERT OR REPLACE INTO fixture_config(Key, Value) VALUES(NEW.Key, NEW.Value);
            END;
            """);

    private Task RestoreCalendarConfigAsync()
        => ExecuteCalendarFixtureSqlAsync("DROP VIEW config; ALTER TABLE fixture_config RENAME TO config;");

    private Task RejectCalendarConfigWriteAsync(string key)
        => ExecuteCalendarFixtureSqlAsync($"""
            CREATE TRIGGER fixture_reject_config BEFORE INSERT ON config WHEN NEW.Key='{key}'
            BEGIN
                SELECT RAISE(ABORT, 'fixture rejected {key}');
            END;
            """);

    private async Task ExecuteCalendarFixtureSqlAsync(string sql)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AwaitCalendarPersistenceWorkAsync(CurrentSeasonViewModel vm)
    {
        await Task.WhenAll(vm.PendingOthersLoad, vm.PendingTimeMachineLoad,
            vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask).WaitAsync(Budget);
        await vm.PendingTimeMachineShownSave.WaitAsync(Budget);
    }

    private static async Task StopCalendarPersistenceAsync(CurrentSeasonViewModel vm, Task loading)
    {
        vm.LoadSeasonalAnimeCommand.Cancel();
        vm.CancelSupplementaryLoads();
        await loading.WaitAsync(Budget);
        await AwaitCalendarPersistenceWorkAsync(vm);
    }
}
