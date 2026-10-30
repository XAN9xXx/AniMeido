using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Player;
using AniMeido.Plugin.Player.Playback;
using AniMeido.Plugin.Player.Services;
using AniMeido.Plugin.Player.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class PlayerExperienceReadWarningTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstOpenReportsItsOwnFallbackEvenAfterPluginInitialization(
        bool settings, bool initialized)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        const string original = "{broken-json";
        await File.WriteAllTextAsync(path, original);
        var store = new PlayerExperienceSettingsStore(path);
        var logger = new ReadLogger();
        if (initialized)
        {
            await new PlayerPlugin(new SourceMappingStore(path: null), store)
                .InitializeAsync(new ServiceCollection());
            Assert.Empty(logger.Entries);
        }

        await ReadAtRealOpenWithoutCreatingAWindowAsync(store, logger, settings);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.IsAssignableFrom<JsonException>(entry.Exception);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FirstOpenWithNormalOrMissingSettingsDoesNotWarn(bool settings, bool exists)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        if (exists)
            await File.WriteAllTextAsync(path, "{\"SchemaVersion\":1,\"Volume\":35}");
        var logger = new ReadLogger();

        await ReadAtRealOpenWithoutCreatingAWindowAsync(
            new PlayerExperienceSettingsStore(path), logger, settings);

        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledOpenDoesNotReportAReadFallback(bool settings)
    {
        using var paths = new MockAppDataPaths();
        var path = Path.Combine(paths.RootDirectory, "experience.json");
        await File.WriteAllTextAsync(path, "{broken-json");
        var logger = new ReadLogger();
        var catalog = new OnlineSourceCatalog(Array.Empty<IOnlineAnimeSource>());
        await using var manager = new PlayerWindowManager(catalog, null!, null!, null!, null!, null!, null!,
            new PlayerExperienceSettingsStore(path), null!, logger: logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (settings
            ? manager.OpenSettingsAsync(PlayerWindowManager.PlayerSettingsId, cancellation.Token)
            : manager.LaunchAsync(new AnimePlaybackContext(42, "Test Anime"), cancellation.Token)).WaitAsync(Budget));

        Assert.Empty(logger.Entries);
    }

    private static async Task ReadAtRealOpenWithoutCreatingAWindowAsync(
        PlayerExperienceSettingsStore store, ReadLogger logger, bool settings)
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var catalog = new OnlineSourceCatalog(() =>
        {
            started.TrySetResult();
            if (!release.Wait(Budget))
                throw new TimeoutException("Synthetic source loading timed out");
            return Array.Empty<IOnlineAnimeSource>();
        });
        await using var manager = new PlayerWindowManager(catalog, null!, null!, null!, null!, null!, null!,
            store, null!, logger: logger);
        var opening = settings
            ? manager.OpenSettingsAsync(PlayerWindowManager.PlayerSettingsId)
            : manager.LaunchAsync(new AnimePlaybackContext(42, "Test Anime"));
        try
        {
            await started.Task.WaitAsync(Budget);
        }
        finally
        {
            try
            {
                await manager.DisposeAsync();
            }
            finally
            {
                release.Set();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => opening.WaitAsync(Budget));
            }
        }
    }

    private sealed record ReadLogEntry(LogLevel Level, Exception? Exception);

    private sealed class ReadLogger : ILogger<PlayerWindowManager>
    {
        public ConcurrentQueue<ReadLogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new ReadLogEntry(logLevel, exception));
    }
}
