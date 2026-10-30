using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class ArchiveCoverRequestsTests
{
    [Theory]
    [InlineData("http")]
    [InlineData("metadata")]
    [InlineData("io")]
    [InlineData("state")]
    [InlineData("timeout")]
    [InlineData("json")]
    public async Task Logging_SharedFailedRequestWarnsOnceForAllWaiters(string kind)
    {
        Exception error = kind switch
        {
            "http" => new HttpRequestException("fixture failure"),
            "metadata" => new MetadataServiceException("fixture failure"),
            "io" => new IOException("fixture failure"),
            "state" => new InvalidOperationException("fixture failure"),
            "timeout" => new TaskCanceledException("fixture failure"),
            _ => new JsonException("fixture failure"),
        };
        var response = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CoverLog();
        var covers = new ArchiveCoverRequests((_, _) => response.Task, logger);
        var thumbnail = covers.GetOrStart(42, default);
        var hero = covers.GetOrStart(42, default);
        try
        {
            Assert.Same(thumbnail, hero);
            response.SetException(error);
            await ObserveAsync(Task.WhenAll(thumbnail, hero)).WaitAsync(Budget);

            Assert.False(thumbnail.IsCompletedSuccessfully);
            var entry = Assert.Single(logger.Entries);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Same(error, entry.Error);
            Assert.Contains("42", entry.Message);
        }
        finally
        {
            response.TrySetResult(null);
            await ObserveAsync(Task.WhenAll(thumbnail, hero)).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_LeavingDoesNotReportCancellationOrLateFailure(bool lateFailure)
    {
        var response = new TaskCompletionSource<Anime?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new CoverLog();
        var covers = new ArchiveCoverRequests((_, _) => response.Task, logger);
        using var lifetime = new CancellationTokenSource();
        var request = covers.GetOrStart(42, lifetime.Token);
        try
        {
            lifetime.Cancel();
            covers.DiscardIncomplete();
            if (lateFailure)
                response.SetException(new HttpRequestException("old request failed"));
            else
                response.SetCanceled(lifetime.Token);
            await ObserveAsync(request).WaitAsync(Budget);

            Assert.Empty(logger.Entries);
        }
        finally
        {
            response.TrySetResult(null);
            await ObserveAsync(request).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Logging_MissingDetailIsSilent()
    {
        var logger = new CoverLog();
        var covers = new ArchiveCoverRequests((_, _) => Task.FromResult<Anime?>(null), logger);
        Assert.Null(await covers.GetOrStart(-1, default).WaitAsync(Budget));
        Assert.Empty(logger.Entries);
    }

    private sealed class CoverLog : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue((level, error, formatter(state, error)));
        }
    }
}
