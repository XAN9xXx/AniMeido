using System.Collections.Concurrent;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class CurrentSeasonLoadLifetimeTests
{
    [Theory]
    [InlineData(LoadFailure.Http, false)]
    [InlineData(LoadFailure.Http, true)]
    [InlineData(LoadFailure.Metadata, false)]
    [InlineData(LoadFailure.Metadata, true)]
    [InlineData(LoadFailure.Json, false)]
    [InlineData(LoadFailure.Json, true)]
    [InlineData(LoadFailure.InvalidOperation, false)]
    [InlineData(LoadFailure.InvalidOperation, true)]
    [InlineData(LoadFailure.Timeout, false)]
    [InlineData(LoadFailure.Timeout, true)]
    public async Task Logging_MainLoadRecordsOnlyAnActiveUnrecoveredFailure(
        LoadFailure failureKind,
        bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var source = new PendingScheduleSource();
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory),
            _featureState,
            new CalendarTestTime(new DateOnly(2026, 9, 18)), logger);
        var failure = CreateFailure(failureKind);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        try
        {
            await source.ScheduleEntered.Task.WaitAsync(Budget);
            if (cancelBeforeFailure)
            {
                vm.LoadSeasonalAnimeCommand.Cancel();
                vm.CancelSupplementaryLoads();
            }

            source.Schedule.SetException(failure);
            await loading.WaitAsync(Budget);

            Assert.Equal(!cancelBeforeFailure, vm.IsError);
            if (cancelBeforeFailure)
            {
                Assert.Empty(logger.Entries);
            }
            else
            {
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(failure, entry.Exception);
            }
        }
        finally
        {
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            source.Schedule.TrySetResult([]);
            await Task.WhenAll(loading, vm.PendingOthersLoad,
                vm.PendingTimeMachineLoad, vm.PendingTimeMachineShownSave).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("season", false)]
    [InlineData("season", true)]
    [InlineData("time-machine", false)]
    [InlineData("time-machine", true)]
    [InlineData("daily-detail", false)]
    [InlineData("daily-detail", true)]
    [InlineData("daily-tags", false)]
    [InlineData("daily-tags", true)]
    public async Task Logging_SupplementaryFailureWarnsOnlyWhileItsRequestIsActive(
        string request,
        bool cancelBeforeFailure)
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentSeason = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("supplementary fixture failure");
        var date = new DateOnly(2026, 9, 18);
        var anime = new Anime(1, "作品1", null, [], new DateOnly(2026, 7, 3), null, "", 2026, 7,
            AnimeListPresentation.ToBangumiWeekday(date.DayOfWeek), 7,
            MediaFormat: AnimeMediaFormat.Television);
        async Task<T> FailAfterReleaseAsync<T>()
        {
            entered.TrySetResult();
            await release.Task;
            throw failure;
        }

        var source = new PendingScheduleSource
        {
            SeasonRequest = (year, _, _) =>
            {
                if (year == date.Year)
                {
                    return request == "season"
                        ? FailAfterReleaseAsync<List<Anime>>()
                        : currentSeason.Task;
                }

                return request == "time-machine"
                    ? FailAfterReleaseAsync<List<Anime>>()
                    : Task.FromResult(new List<Anime>());
            },
            DetailRequest = (_, _) => request == "daily-detail"
                ? FailAfterReleaseAsync<Anime?>()
                : Task.FromResult<Anime?>(anime),
            TagsRequest = (_, _) => request == "daily-tags"
                ? FailAfterReleaseAsync<List<Tag>>()
                : Task.FromResult(new List<Tag>()),
        };
        var logger = new CalendarLog();
        var vm = new CurrentSeasonViewModel(source, new TrackingService(DbFactory),
            _featureState,
            new CalendarTestTime(date), logger);
        source.Schedule.SetResult([anime]);
        var loading = vm.LoadSeasonalAnimeCommand.ExecuteAsync(null);
        Task details = Task.CompletedTask;
        try
        {
            await loading.WaitAsync(Budget);
            details = vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask;
            await entered.Task.WaitAsync(Budget);
            currentSeason.TrySetResult([]);
            if (cancelBeforeFailure)
                vm.CancelSupplementaryLoads();

            release.SetResult();
            await Task.WhenAll(details, vm.PendingOthersLoad, vm.PendingTimeMachineLoad).WaitAsync(Budget);

            Assert.True(vm.HasData);
            Assert.False(vm.IsError);
            if (cancelBeforeFailure)
            {
                Assert.Empty(logger.Entries);
            }
            else
            {
                var entry = Assert.Single(logger.Entries);
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Same(failure, entry.Exception);
            }
        }
        finally
        {
            vm.LoadSeasonalAnimeCommand.Cancel();
            vm.CancelSupplementaryLoads();
            currentSeason.TrySetResult([]);
            release.TrySetResult();
            await Task.WhenAll(loading, details,
                vm.LoadDailyPickDetailsCommand.ExecutionTask ?? Task.CompletedTask,
                vm.PendingOthersLoad, vm.PendingTimeMachineLoad,
                vm.PendingTimeMachineShownSave).WaitAsync(Budget);
        }
    }

    private sealed record CalendarLogEntry(LogLevel Level, Exception? Exception);

    private sealed class CalendarLog : ILogger
    {
        public ConcurrentQueue<CalendarLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new CalendarLogEntry(level, exception));
        }
    }
}
