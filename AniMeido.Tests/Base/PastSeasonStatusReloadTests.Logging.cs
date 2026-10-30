using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class PastSeasonStatusReloadTests
{
    private static readonly TimeSpan LoggingBudget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_CurrentSeasonFailureIsOneErrorWithTheOriginalException(bool timeout)
    {
        Exception error = timeout ? new TaskCanceledException("season timeout") : new HttpRequestException("season offline");
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource
        {
            Load = (_, _, _) => Task.FromException<List<Anime>>(error),
        }, new TrackingService(DbFactory), logger);

        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring).WaitAsync(LoggingBudget);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(error, entry.Exception);
        Assert.True(vm.IsError);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldSeasonFailureOrExplicitCancellationIsSilent(bool cancel)
    {
        var pending = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource
        {
            Load = (year, _, _) => year == 2025 ? pending.Task : Task.FromResult(new List<Anime> { Item(2) }),
        }, new TrackingService(DbFactory), logger);
        var old = vm.LoadPastSeasonAnimeAsync(2025, Season.Spring, cancellation.Token);
        Task current = Task.CompletedTask;
        try
        {
            if (cancel)
                cancellation.Cancel();
            else
            {
                current = vm.LoadPastSeasonAnimeAsync(2026, Season.Spring);
                await current.WaitAsync(LoggingBudget);
            }
            pending.TrySetException(cancel
                ? new TaskCanceledException("cancelled season", null, cancellation.Token)
                : new HttpRequestException("old season offline"));
            await old.WaitAsync(LoggingBudget);

            Assert.Empty(logger.Entries);
            Assert.False(vm.IsError);
            if (!cancel)
                Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
        }
        finally
        {
            pending.TrySetResult([]);
            await LoggingObserveAsync(old, current).WaitAsync(LoggingBudget);
        }
    }

    [Fact]
    public async Task Logging_CurrentStatusFailureWarnsAndKeepsThePreviousMarks()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, logger);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring).WaitAsync(LoggingBudget);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
        Assert.True(await vm.ReloadStatusesAsync().WaitAsync(LoggingBudget));
        var entries = vm.Entries;
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE tracking";
            await command.ExecuteNonQueryAsync();
        }

        Assert.False(await vm.ReloadStatusesAsync().WaitAsync(LoggingBudget));

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(1, Assert.IsType<SqliteException>(entry.Exception).SqliteErrorCode);
        Assert.Same(entries, vm.Entries);
        AssertCurrentSnapshot(vm, AnimeTrackingStatus.Completed, false);
    }

    [Fact]
    public async Task Logging_OldStatusFailureIsSilentAfterANewerStatusReadSucceeds()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var logger = new SeasonLog();
        var vm = new PastSeasonViewModel(new SeasonSource(), tracking, logger);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Spring).WaitAsync(LoggingBudget);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE tracking";
            await command.ExecuteNonQueryAsync();
        }
        var (context, old) = await PauseStatusesBeforePublicationAsync(vm);
        Task<bool> current = Task.FromResult(false);
        try
        {
            using (var connection = await DbFactory.OpenAsync())
                await PersonalDatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Completed);
            current = vm.ReloadStatusesAsync();
            Assert.True(await current.WaitAsync(LoggingBudget));
            context.Resume();
            Assert.False(await old.WaitAsync(LoggingBudget));

            Assert.Empty(logger.Entries);
            AssertCurrentSnapshot(vm, AnimeTrackingStatus.Completed, false);
        }
        finally
        {
            context.Resume();
            await LoggingObserveAsync(old, current).WaitAsync(LoggingBudget);
        }
    }

    private static async Task LoggingObserveAsync(params Task[] tasks)
        => await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class SeasonLog : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue((level, exception, formatter(state, exception)));
        }
    }
}
