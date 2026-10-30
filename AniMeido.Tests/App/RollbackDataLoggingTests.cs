using AniMeido.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class RollbackDataLoggingTests
{
    [Fact]
    public async Task StorePropagatesDeletionFailureWithoutLoggingItBeforeTheCaller()
    {
        using var paths = new MockAppDataPaths();
        var directory = Path.Combine(paths.BackupDirectory, "update", "AniMeido-before-restore-20261026-120000");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "saved.db");
        await File.WriteAllTextAsync(path, "synthetic data");
        var events = new CapturedEvents();
        using var logger = new LoggerConfiguration().WriteTo.Sink(events).CreateLogger();
        var previous = Log.Logger;
        Log.Logger = logger;
        try
        {
            await using var store = new RollbackDataStore(paths);
            using var held = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            var error = await Record.ExceptionAsync(() => store.DeleteAsync(directory));

            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(events.Items);
            Assert.True(File.Exists(path));
        }
        finally
        {
            Log.Logger = previous;
        }
    }

    private sealed class CapturedEvents : ILogEventSink
    {
        public ConcurrentBag<LogEvent> Items { get; } = [];

        public void Emit(LogEvent logEvent) => Items.Add(logEvent);
    }
}
