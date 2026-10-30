using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class StartupMigrationLoggingTests : DbTestBase
{
    [Fact]
    public async Task ClosingDuringMigrationDoesNotLogNormalCancellation()
    {
        using var logs = new CapturedStartupLog();
        using var cancellation = new CancellationTokenSource();
        var cancelled = new OperationCanceledException(cancellation.Token);
        var gate = new StartupMigrationGate(_ =>
        {
            cancellation.Cancel();
            return Task.FromException(cancelled);
        });

        Assert.False(await gate.TryRunAsync(cancellation.Token));

        Assert.False(gate.IsRunning);
        Assert.False(gate.IsReady);
        Assert.Empty(logs.Events);
    }

    [Fact]
    public async Task ClosingAfterMigrationReturnsDoesNotLogTheFinalCancellationCheck()
    {
        using var logs = new CapturedStartupLog();
        using var cancellation = new CancellationTokenSource();
        var gate = new StartupMigrationGate(_ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        Assert.False(await gate.TryRunAsync(cancellation.Token));

        Assert.False(gate.IsReady);
        Assert.False(gate.IsRunning);
        Assert.Empty(logs.Events);
    }

    [Fact]
    public async Task FailedMigrationIsLoggedOnceByTheMigrationOperationAsAnError()
    {
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 123";
            await command.ExecuteNonQueryAsync();
        }
        using var logs = new CapturedStartupLog();
        using var http = new HttpClient();
        var operationLog = new MigrationLogger();
        var migration = new PublicIdMigrationService(DbFactory, http, operationLog);
        var gate = new StartupMigrationGate(ct => migration.MigrateAsync(ct));

        Assert.False(await gate.TryRunAsync());

        Assert.Empty(logs.Events);
        var entry = Assert.Single(operationLog.Events);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<InvalidDataException>(entry.Error);
        Assert.False(gate.IsReady);
        Assert.False(gate.IsRunning);
    }

    private sealed class CapturedStartupLog : IDisposable
    {
        private readonly Serilog.ILogger _previous = Log.Logger;
        private readonly EventSink _sink = new();
        private readonly Logger _logger;

        public CapturedStartupLog()
        {
            _logger = new LoggerConfiguration().WriteTo.Sink(_sink).CreateLogger();
            Log.Logger = _logger;
        }

        public List<LogEvent> Events => _sink.Events;

        public void Dispose()
        {
            Log.Logger = _previous;
            _logger.Dispose();
        }

        private sealed class EventSink : ILogEventSink
        {
            public List<LogEvent> Events { get; } = [];

            public void Emit(LogEvent logEvent) => Events.Add(logEvent);
        }
    }

    private sealed class MigrationLogger : ILogger<PublicIdMigrationService>
    {
        public List<(LogLevel Level, Exception? Error)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, exception));
    }
}
