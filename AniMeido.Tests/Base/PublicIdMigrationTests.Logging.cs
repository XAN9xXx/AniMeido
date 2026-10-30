using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class PublicIdMigrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migration_NonCancellationFailureDuringCancellationStillLogsOneError(bool malformedJson)
    {
        await CreateV8Async();
        await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        using var cancellation = new CancellationTokenSource();
        Exception failure = malformedJson
            ? new JsonException("Synthetic migration response failure.")
            : new InvalidDataException("Synthetic migration data failure.");
        using var handler = new CancellationOutcomeHandler(cancellation, failure);
        using var client = Client(handler);
        var logger = new CapturedMigrationLogger();
        var migration = new PublicIdMigrationService(DbFactory, client, logger);

        var actual = await Record.ExceptionAsync(() => migration.MigrateAsync(cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.RequestCount);
        Assert.Same(failure, actual);
        var entry = Assert.Single(logger.Events);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Error);
        Assert.Equal(8, await VersionAsync());
    }

    [Fact]
    public async Task Migration_NormalCancellationDoesNotLogAnError()
    {
        await CreateV8Async();
        await ExecuteAsync("INSERT INTO tracking VALUES(1,1,@at)");
        using var cancellation = new CancellationTokenSource();
        using var handler = new CancellationOutcomeHandler(
            cancellation, new OperationCanceledException(cancellation.Token));
        using var client = Client(handler);
        var logger = new CapturedMigrationLogger();
        var migration = new PublicIdMigrationService(DbFactory, client, logger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => migration.MigrateAsync(cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, handler.RequestCount);
        Assert.Empty(logger.Events);
        Assert.Equal(8, await VersionAsync());
    }

    private sealed class CancellationOutcomeHandler(
        CancellationTokenSource cancellation,
        Exception failure) : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            cancellation.Cancel();
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }

    private sealed class CapturedMigrationLogger : ILogger<PublicIdMigrationService>
    {
        internal List<(LogLevel Level, Exception? Error)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, exception));
    }
}
