using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed partial class TagSearchResultsResumeTests
{
    private const string LoggingPrivateTag = "private-tag-marker-419";

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    public async Task Logging_CurrentQueryFailureIsOneErrorWithoutTheTag(string kind)
    {
        var error = Failure(kind);
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => Task.FromException<List<Anime>>(error),
            () => Task.FromResult(new HashSet<int>()), logger);

        await vm.InitializeAsync(LoggingPrivateTag, 2023, 2026).WaitAsync(Budget);

        var entry = LoggingSingleEntry(logger, LogLevel.Error);
        Assert.Same(error, entry.Exception);
        Assert.Empty(vm.Items);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_OldQueryFailureOrExplicitSuspendIsSilent(bool cancel)
    {
        var pending = ResultsGate();
        var entered = Signal();
        var oldToken = CancellationToken.None;
        var queries = 0;
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel((_, _, _, token) =>
        {
            if (++queries != 1)
                return Task.FromResult(Works(2));
            oldToken = token;
            entered.TrySetResult();
            return pending.Task;
        }, () => Task.FromResult(new HashSet<int>()), logger);
        var old = vm.InitializeAsync(LoggingPrivateTag, 2023, 2026);
        Task current = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Budget);
            if (cancel)
                vm.Suspend();
            else
            {
                current = vm.ChangeYearRangeAsync(2022, 2026);
                await current.WaitAsync(Budget);
            }
            pending.TrySetException(cancel ? new OperationCanceledException(oldToken) : Failure("http"));
            await old.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            if (!cancel)
                Assert.Equal(2, vm.TotalCount);
        }
        finally
        {
            pending.TrySetResult([]);
            await LoggingObserveAsync(old, current).WaitAsync(Budget);
            vm.Suspend();
        }
    }

    [Fact]
    public async Task Logging_NonExplicitCancellationKeepsTheExistingSilentContract()
    {
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => Task.FromException<List<Anime>>(new OperationCanceledException("existing cancellation contract")),
            () => Task.FromResult(new HashSet<int>()), logger);

        await vm.InitializeAsync(LoggingPrivateTag, 2023, 2026).WaitAsync(Budget);

        Assert.Empty(logger.Entries);
        Assert.False(vm.IsLoading);
        Assert.False(vm.HasPages);
    }

    [Fact]
    public async Task Logging_BlockedReadFailureWarnsButTheQueryStillPublishes()
    {
        var error = new IOException("blocked list unavailable");
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => Task.FromResult(Works(2)),
            () => Task.FromException<HashSet<int>>(error), logger);

        await vm.InitializeAsync(LoggingPrivateTag, 2023, 2026).WaitAsync(Budget);

        var entry = LoggingSingleEntry(logger, LogLevel.Warning);
        Assert.Same(error, entry.Exception);
        Assert.Equal(2, vm.TotalCount);
        Assert.False(vm.IsLoading);
    }

    [Fact]
    public async Task Logging_OldBlockedReadFailureIsSilentAfterANewerQuerySucceeded()
    {
        var blocked = new TaskCompletionSource<HashSet<int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = Signal();
        var reads = 0;
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => Task.FromResult(Works(2)),
            () =>
            {
                if (++reads != 1)
                    return Task.FromResult(new HashSet<int>());
                entered.TrySetResult();
                return blocked.Task;
            }, logger);
        var old = vm.InitializeAsync(LoggingPrivateTag, 2023, 2026);
        Task current = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(Budget);
            current = vm.ChangeYearRangeAsync(2022, 2026);
            await current.WaitAsync(Budget);
            blocked.TrySetException(new IOException("old blocked read unavailable"));
            await old.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.Equal(2, vm.TotalCount);
        }
        finally
        {
            blocked.TrySetResult([]);
            await LoggingObserveAsync(old, current).WaitAsync(Budget);
            vm.Suspend();
        }
    }

    [Fact]
    public async Task Logging_YearRangeValidationDoesNotProduceAnErrorLog()
    {
        var logger = new TagLog();
        var vm = new TagSearchResultsViewModel(
            (_, _, _, _) => throw new NotSupportedException("validation must not query"),
            () => throw new NotSupportedException("validation must not read blocked records"), logger);

        await vm.InitializeAsync(LoggingPrivateTag, 2026, 2025).WaitAsync(Budget);

        Assert.Empty(logger.Entries);
        Assert.Equal("起始年份不能晚于结束年份", vm.ResultText);
    }

    private static (LogLevel Level, Exception? Exception, string Message) LoggingSingleEntry(TagLog logger, LogLevel level)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.DoesNotContain(LoggingPrivateTag, entry.Message);
        return entry;
    }

    private static async Task LoggingObserveAsync(params Task[] tasks)
        => await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class TagLog : ILogger
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
