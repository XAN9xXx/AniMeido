using AniMeido.App.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class LoggingLifetimeTests
{
    [Fact]
    public async Task DisposingServices_LeavesTheApplicationLoggerAvailableForFinalShutdownReporting()
    {
        var previous = Log.Logger;
        var sink = new OwnedSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        Log.Logger = logger;
        try
        {
            await using var provider = new ServiceCollection().AddAppServices().BuildServiceProvider();
            _ = provider.GetRequiredService<ILogger<LoggingLifetimeTests>>();

            await provider.DisposeAsync();

            Assert.False(sink.Disposed);
            Log.Warning("fixture final shutdown result");
            Assert.Single(sink.Events);
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class OwnedSink : ILogEventSink, IDisposable
    {
        internal bool Disposed { get; private set; }
        internal List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);

        public void Dispose() => Disposed = true;
    }
}
