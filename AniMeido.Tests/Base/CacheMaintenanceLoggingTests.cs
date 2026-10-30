using AniMeido.App.Services;
using AniMeido.Contracts;
using AniMeido.Plugin.Base;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace AniMeido.Tests;

public sealed class CacheMaintenanceLoggingTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CleanupFailure_FromDesktopCompositionReportsOneWarningWithoutInterruptingTheCaller()
    {
        using var paths = new MockAppDataPaths();
        var logs = new RecordingLoggerProvider();
        using var provider = await CreateProviderAsync(paths, logs);
        var factory = provider.GetRequiredService<SqliteConnectionFactory>();
        var cache = provider.GetRequiredService<CacheService>();
        await new DatabaseService(factory, paths).InitializeAsync();
        using (var connection = await factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE cache";
            await command.ExecuteNonQueryAsync();
        }

        await cache.CleanExpiredAsync().WaitAsync(Budget);

        var warning = Assert.Single(logs.Entries);
        Assert.Equal(typeof(CacheService).FullName, warning.Category);
        Assert.Equal(LogLevel.Warning, warning.Level);
        var failure = Assert.IsType<SqliteException>(warning.Exception);
        Assert.Equal(1, failure.SqliteErrorCode);
        Assert.Contains("cache", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(warning.Message));
    }

    [Fact]
    public async Task ImportBusy_SkipsCleanupSilentlyAndKeepsTheExpiredRecord()
    {
        using var paths = new MockAppDataPaths();
        var logs = new RecordingLoggerProvider();
        using var provider = await CreateProviderAsync(paths, logs);
        var factory = provider.GetRequiredService<SqliteConnectionFactory>();
        var cache = provider.GetRequiredService<CacheService>();
        await new DatabaseService(factory, paths).InitializeAsync();
        await cache.SetCacheAsync("retained-while-importing", "old-data", TimeSpan.FromDays(-31));
        using (var import = factory.Access.BeginExclusive())
        {
            await import.WaitForDrainAsync(CancellationToken.None);
            Task cleanup;
            using (ExecutionContext.SuppressFlow())
            {
                cleanup = Task.Run(cache.CleanExpiredAsync);
            }

            await cleanup.WaitAsync(Budget);
            Assert.Empty(logs.Entries);
        }

        using var connection = await factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM cache WHERE CacheKey = 'retained-while-importing'";
        Assert.Equal("old-data", await command.ExecuteScalarAsync());
    }

    private static async Task<ServiceProvider> CreateProviderAsync(
        MockAppDataPaths paths,
        RecordingLoggerProvider logs)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAppDataPaths>(paths);
        services.AddLogging(logging => logging.ClearProviders().AddProvider(logs));
        await new BasePlugin().InitializeAsync(services);
        return services.BuildServiceProvider();
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    private sealed class RecordingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (category == typeof(CacheService).FullName && IsEnabled(logLevel))
            {
                entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception), exception));
            }
        }
    }
}
