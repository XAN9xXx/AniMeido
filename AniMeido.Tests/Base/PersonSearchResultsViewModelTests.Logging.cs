using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class PersonSearchResultsViewModelTests
{
    private static readonly TimeSpan LoggingBudget = TimeSpan.FromSeconds(5);
    private const string LoggingPrivateName = "private-person-marker-418";

    [Fact]
    public async Task Logging_SharedLoadAndResumeReportOneCurrentListErrorNotOnePerWaiter()
    {
        var pending = NewCompletion<List<PersonWork>>();
        var started = NewCompletion();
        var error = new HttpRequestException("person works offline");
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                started.TrySetResult();
                return pending.Task;
            },
        }, new TrackingService(DbFactory), logger);
        var first = vm.LoadAsync(7, LoggingPrivateName);
        Task resumed = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(LoggingBudget);
            resumed = vm.ResumeAsync();
            Assert.False(resumed.IsCompleted);
            pending.TrySetException(error);
            await Task.WhenAll(first, resumed).WaitAsync(LoggingBudget);

            var entry = LoggingSingleEntry(logger, LogLevel.Error);
            Assert.Same(error, entry.Exception);
            Assert.Contains("加载失败", vm.ResultText);
        }
        finally
        {
            pending.TrySetResult([]);
            await LoggingObserveAsync(first, resumed).WaitAsync(LoggingBudget);
            vm.CancelLoad();
        }
    }

    [Fact]
    public async Task Logging_DetailFallbackWarnsOnceAndKeepsTheWorkSnapshot()
    {
        await RunProductionMigrationAsync();
        var error = Failure("json");
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromResult(new List<PersonWork> { Work(1) }),
            Detail = (_, _) => Task.FromException<Anime?>(error),
        }, new TrackingService(DbFactory), logger);
        try
        {
            await vm.LoadAsync(7, LoggingPrivateName).WaitAsync(LoggingBudget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            Assert.Same(error, entry.Exception);
            Assert.Equal("名单1", Assert.Single(vm.Results).Title);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            vm.CancelLoad();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldListFailureOrExplicitCancellationIsSilent(bool cancel)
    {
        await RunProductionMigrationAsync();
        var pending = NewCompletion<List<PersonWork>>();
        var started = NewCompletion();
        var oldToken = CancellationToken.None;
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (id, token) =>
            {
                if (id != 7)
                    return Task.FromResult(new List<PersonWork> { Work(2) });
                oldToken = token;
                started.TrySetResult();
                return pending.Task;
            },
        }, new TrackingService(DbFactory), logger);
        var old = vm.LoadAsync(7, LoggingPrivateName);
        Task current = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(LoggingBudget);
            if (cancel)
                vm.CancelLoad();
            else
            {
                current = vm.LoadAsync(8, "current person");
                await current.WaitAsync(LoggingBudget);
            }
            pending.TrySetException(cancel ? new OperationCanceledException(oldToken) : new HttpRequestException("old works offline"));
            await old.WaitAsync(LoggingBudget);

            Assert.Empty(logger.Entries);
            if (!cancel)
                Assert.Equal(2, Assert.Single(vm.Results).ID);
        }
        finally
        {
            pending.TrySetResult([]);
            await LoggingObserveAsync(old, current).WaitAsync(LoggingBudget);
            vm.CancelLoad();
        }
    }

    [Fact]
    public async Task Logging_OldDetailFallbackIsSilentAfterAnotherPersonWasLoaded()
    {
        await RunProductionMigrationAsync();
        var pending = NewCompletion<Anime?>();
        var started = NewCompletion();
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (id, _) => Task.FromResult(new List<PersonWork> { Work(id == 7 ? 1 : 2) }),
            Detail = (id, _) =>
            {
                if (id != 1)
                    return Task.FromResult<Anime?>(Item(2));
                started.TrySetResult();
                return pending.Task;
            },
        }, new TrackingService(DbFactory), logger);
        var old = vm.LoadAsync(7, LoggingPrivateName);
        Task current = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(LoggingBudget);
            current = vm.LoadAsync(8, "current person");
            await current.WaitAsync(LoggingBudget);
            pending.TrySetException(Failure("http"));
            await old.WaitAsync(LoggingBudget);

            Assert.Empty(logger.Entries);
            Assert.Equal(2, Assert.Single(vm.Results).ID);
        }
        finally
        {
            pending.TrySetResult(Item(1));
            await LoggingObserveAsync(old, current).WaitAsync(LoggingBudget);
            vm.CancelLoad();
        }
    }

    [Fact]
    public async Task Logging_NonExplicitCancellationKeepsTheExistingSilentContract()
    {
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) => Task.FromException<List<PersonWork>>(new OperationCanceledException("existing cancellation contract")),
        }, new TrackingService(DbFactory), logger);

        await vm.LoadAsync(7, LoggingPrivateName).WaitAsync(LoggingBudget);

        Assert.Empty(logger.Entries);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Logging_BlockedRefreshFailureWarnsAndKeepsThePublishedWorks()
    {
        await RunProductionMigrationAsync();
        var calls = 0;
        var logger = new PersonLog();
        var vm = new PersonSearchResultsViewModel(new PersonSource
        {
            Works = (_, _) =>
            {
                calls++;
                return Task.FromResult(new List<PersonWork> { Work(1) });
            },
        }, new TrackingService(DbFactory), logger);
        await vm.LoadAsync(7, LoggingPrivateName).WaitAsync(LoggingBudget);
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE tracking";
            await command.ExecuteNonQueryAsync();
        }
        try
        {
            await vm.ResumeAsync().WaitAsync(LoggingBudget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            Assert.Equal(1, Assert.IsType<SqliteException>(entry.Exception).SqliteErrorCode);
            Assert.Equal(1, Assert.Single(vm.Results).ID);
            Assert.Equal(1, calls);
        }
        finally
        {
            vm.CancelLoad();
        }
    }

    private static (LogLevel Level, Exception? Exception, string Message) LoggingSingleEntry(PersonLog logger, LogLevel level)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.DoesNotContain(LoggingPrivateName, entry.Message);
        return entry;
    }

    private static async Task LoggingObserveAsync(params Task[] tasks)
        => await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class PersonLog : ILogger
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
