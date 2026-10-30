using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class GlobalSearchViewModelTests
{
    private const string LoggingPrivateQuery = "private-search-marker-416";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_CurrentInitialOrNextPageFailureIsOneErrorWithoutTheQuery(bool nextPage)
    {
        await RunProductionMigrationAsync();
        var error = new HttpRequestException("remote failure");
        var source = new SearchSource
        {
            Search = (_, offset, _) => nextPage && offset == 0
                ? Task.FromResult((new List<Anime> { Item(1) }, 2))
                : Task.FromException<(List<Anime>, int)>(error),
        };
        var logger = new SearchLog();
        var vm = NewViewModel(source, new TrackingService(DbFactory), logger);
        try
        {
            await vm.SearchAsync(LoggingPrivateQuery).WaitAsync(Budget);
            if (nextPage)
                await vm.LoadMoreAsync().WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Error);
            Assert.Same(error, entry.Exception);
            Assert.True(vm.IsError);
            Assert.False(vm.IsSearching);
            Assert.False(vm.IsLoadingMore);
        }
        finally
        {
            vm.CancelPendingSearch();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldRemoteFailureOrExplicitCancellationIsSilent(bool cancel)
    {
        await RunProductionMigrationAsync();
        var pending = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldToken = CancellationToken.None;
        var source = new SearchSource
        {
            Search = (query, _, token) =>
            {
                if (query != LoggingPrivateQuery)
                    return Task.FromResult((new List<Anime> { Item(42) }, 1));
                oldToken = token;
                started.TrySetResult();
                return pending.Task;
            },
        };
        var logger = new SearchLog();
        var vm = NewViewModel(source, new TrackingService(DbFactory), logger);
        Task old = Task.CompletedTask;
        Task current = Task.CompletedTask;
        try
        {
            old = vm.SearchAsync(LoggingPrivateQuery);
            await started.Task.WaitAsync(Budget);
            if (cancel)
                vm.CancelPendingSearch();
            else
            {
                current = vm.SearchAsync("current-search");
                await current.WaitAsync(Budget);
            }
            Assert.True(oldToken.IsCancellationRequested);
            pending.TrySetException(cancel
                ? new OperationCanceledException(oldToken)
                : new HttpRequestException("old request failure"));
            await old.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.False(vm.IsError);
            if (!cancel)
                Assert.Equal(42, Assert.Single(vm.Results).Anime.ID);
        }
        finally
        {
            pending.TrySetResult(([], 0));
            await LoggingObserveAsync(old, current).WaitAsync(Budget);
            vm.CancelPendingSearch();
        }
    }

    [Fact]
    public async Task Logging_LocalBranchFinalFailureIsOneWarningAndRemoteResultsStillPublish()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var error = new JsonException("local detail parse failure");
        var source = new SearchSource
        {
            Detail = (_, _) => Task.FromException<Anime?>(error),
            Search = (_, _, _) => Task.FromResult((new List<Anime> { Item(42) }, 1)),
        };
        var logger = new SearchLog();
        var vm = NewViewModel(source, tracking, logger);
        try
        {
            await vm.SearchAsync(LoggingPrivateQuery).WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            Assert.Same(error, entry.Exception);
            Assert.False(vm.IsError);
            Assert.Empty(vm.LocalMatches);
            Assert.Equal(42, Assert.Single(vm.Results).Anime.ID);
        }
        finally
        {
            vm.CancelPendingSearch();
        }
    }

    [Fact]
    public async Task Logging_RecentSearchSaveFailureWarnsButDoesNotStopTheSearch()
    {
        await RunProductionMigrationAsync();
        await DropSearchConfigAsync();
        var logger = new SearchLog();
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) => Task.FromResult((new List<Anime> { Item(42) }, 1)),
        }, new TrackingService(DbFactory), logger);
        try
        {
            await vm.SearchAsync(LoggingPrivateQuery).WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            var error = Assert.IsType<SqliteException>(entry.Exception);
            Assert.Equal(1, error.SqliteErrorCode);
            Assert.Contains("config", error.Message);
            Assert.False(vm.IsError);
            Assert.Equal(42, Assert.Single(vm.Results).Anime.ID);
            Assert.Equal(LoggingPrivateQuery, Assert.Single(vm.RecentSearches));
        }
        finally
        {
            vm.CancelPendingSearch();
        }
    }

    [Fact]
    public async Task Logging_StatusRefreshFailureWarnsAndKeepsThePublishedStatuses()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        await tracking.SetStatusAsync(1, AnimeTrackingStatus.Following);
        var logger = new SearchLog();
        var vm = NewViewModel(new SearchSource
        {
            Search = (_, _, _) => Task.FromResult((new List<Anime> { Item(1) }, 1)),
        }, tracking, logger);
        try
        {
            await vm.SearchAsync(LoggingPrivateQuery).WaitAsync(Budget);
            Assert.Empty(logger.Entries);
            using (var connection = await DbFactory.OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TABLE tracking";
                await command.ExecuteNonQueryAsync();
            }
            await vm.RefreshStatusesAsync().WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            var error = Assert.IsType<SqliteException>(entry.Exception);
            Assert.Equal(1, error.SqliteErrorCode);
            Assert.Contains("tracking", error.Message);
            Assert.Equal(AnimeTrackingStatus.Following, Assert.Single(vm.Results).Status);
            Assert.False(vm.IsError);
        }
        finally
        {
            vm.CancelPendingSearch();
        }
    }

    private static SearchLogEntry LoggingSingleEntry(SearchLog logger, LogLevel level)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.DoesNotContain(LoggingPrivateQuery, entry.Message);
        Assert.NotNull(entry.Exception);
        return entry;
    }

    private static async Task LoggingObserveAsync(params Task[] tasks)
    {
        // 先放行门控，再观察实际任务；等待预算由调用方控制。
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed record SearchLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class SearchLog : ILogger
    {
        public ConcurrentQueue<SearchLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new SearchLogEntry(level, exception, formatter(state, exception)));
        }
    }
}
