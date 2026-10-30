using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class ManagementViewModelTests
{
    private const string LoggingPrivateQuery = "private-management-search-marker-417";

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    public async Task Logging_CurrentTagFailureIsOneErrorWithTheOriginalException(string kind)
    {
        var error = Failure(kind, "current tag request failure");
        var source = new ManagementSource
        {
            TagSearch = (_, _) => Task.FromException<(List<Anime>, int)>(error),
        };
        var logger = new MineLog();
        var vm = await CreateViewModelAsync(source, logger);
        try
        {
            await vm.ShowTagsAsync().WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Error);
            Assert.Same(error, entry.Exception);
            Assert.True(vm.IsError);
            Assert.False(vm.IsTagLoading);
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }

    [Fact]
    public async Task Logging_CurrentTrackedSearchFailureIsOneErrorWithoutTheQuery()
    {
        var logger = new MineLog();
        var vm = await CreateViewModelAsync(new ManagementSource(), logger);
        try
        {
            using (var connection = await DbFactory.OpenAsync())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "DROP TABLE tracking";
                await command.ExecuteNonQueryAsync();
            }
            await vm.SearchAsync(LoggingPrivateQuery).WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Error);
            var error = Assert.IsType<SqliteException>(entry.Exception);
            Assert.Equal(1, error.SqliteErrorCode);
            Assert.Contains("tracking", error.Message);
            Assert.True(vm.IsError);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldTagFailureOrExplicitCancellationIsSilent(bool cancel)
    {
        var pending = new TaskCompletionSource<(List<Anime>, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldToken = CancellationToken.None;
        var source = new ManagementSource
        {
            TagSearch = (tag, token) =>
            {
                if (tag != "A-old")
                    return Task.FromResult((new List<Anime> { Item(2) }, 1));
                oldToken = token;
                started.TrySetResult();
                return pending.Task;
            },
        };
        var logger = new MineLog();
        var vm = await CreateViewModelAsync(source, logger);
        Task old = Task.CompletedTask;
        Task current = Task.CompletedTask;
        try
        {
            old = vm.ShowTagsAsync();
            await started.Task.WaitAsync(Budget);
            if (cancel)
                vm.CancelPendingLoads();
            else
            {
                current = vm.SelectTagAsync("B-new");
                await current.WaitAsync(Budget);
            }
            Assert.True(oldToken.IsCancellationRequested);
            pending.TrySetException(cancel
                ? new OperationCanceledException(oldToken)
                : new HttpRequestException("old tag request failure"));
            await old.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.False(vm.IsError);
            if (!cancel)
                AssertCurrentTagView(vm);
        }
        finally
        {
            pending.TrySetResult(([], 0));
            await LoggingObserveAsync(old, current).WaitAsync(Budget);
            vm.CancelPendingLoads();
        }
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    public async Task Logging_SkippingOneFailedDetailWarnsAndKeepsTheOtherEntries(string kind)
    {
        var error = Failure(kind, "one detail failed");
        var source = new ManagementSource
        {
            Detail = (id, _) => id == 2
                ? Task.FromException<Anime?>(error)
                : Task.FromResult<Anime?>(Item(id)),
        };
        var logger = new MineLog();
        var vm = await CreateViewModelAsync(source, logger);
        try
        {
            await new TrackingService(DbFactory).SetStatusAsync(2, AnimeTrackingStatus.Watching);
            await vm.LoadAsync().WaitAsync(Budget);

            var entry = LoggingSingleEntry(logger, LogLevel.Warning);
            Assert.Same(error, entry.Exception);
            Assert.Contains("2", entry.Message);
            Assert.Equal(1, Assert.Single(vm.Entries).Anime.ID);
            Assert.Equal(2, vm.SelectedSection.Count);
            Assert.False(vm.IsError);
        }
        finally
        {
            vm.CancelPendingLoads();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldDetailFailureOrExplicitCancellationDoesNotWarn(bool cancel)
    {
        var pending = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new ManagementSource
        {
            Detail = (id, _) =>
            {
                if (id != 2)
                    return Task.FromResult<Anime?>(Item(id));
                started.TrySetResult();
                return pending.Task;
            },
        };
        var logger = new MineLog();
        var vm = await CreateViewModelAsync(source, logger);
        Task old = Task.CompletedTask;
        Task current = Task.CompletedTask;
        try
        {
            await new TrackingService(DbFactory).SetStatusAsync(2, AnimeTrackingStatus.Watching);
            old = vm.LoadAsync();
            await started.Task.WaitAsync(Budget);
            if (cancel)
                vm.CancelPendingLoads();
            else
            {
                current = vm.SelectSectionAsync(vm.StatusSections.Single(
                    section => section.Status == AnimeTrackingStatus.Completed));
                await current.WaitAsync(Budget);
            }
            pending.TrySetException(new JsonException("old detail request failure"));
            await old.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.False(vm.IsError);
            Assert.False(vm.IsLoading);
        }
        finally
        {
            pending.TrySetResult(Item(2));
            await LoggingObserveAsync(old, current).WaitAsync(Budget);
            vm.CancelPendingLoads();
        }
    }

    private static MineLogEntry LoggingSingleEntry(MineLog logger, LogLevel level)
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

    private sealed record MineLogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class MineLog : ILogger
    {
        public ConcurrentQueue<MineLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new MineLogEntry(level, exception, formatter(state, exception)));
        }
    }
}
