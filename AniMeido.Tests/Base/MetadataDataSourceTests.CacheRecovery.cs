using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed partial class MetadataDataSourceTests
{
    [Fact]
    public async Task LateCorruptCacheCleanupPreservesAConcurrentReplacementWithoutAnotherDownload()
    {
        await CreateBaseTablesAsync();
        const string key = "metadata:detail:42";
        var budget = TimeSpan.FromSeconds(5);
        var cache = new CacheService(DbFactory);
        await cache.SetCacheAsync(key, "broken-json", TimeSpan.FromDays(1));
        using var handler = new StubHandler(_ => Raw(DetailJson));
        using var client = Client(handler);
        using var log = new PausedInvalidCacheWarning(budget);
        var source = new MetadataDataSource(log, client, cache, new TestTime());
        var oldRead = Task.Run(() => source.GetAnimeDetailAsync(42, CancellationToken.None));
        Task<Anime?>? replacementRead = null;
        Exception? primaryFailure = null;
        try
        {
            await log.Paused.WaitAsync(budget);
            Assert.False(oldRead.IsCompleted);

            replacementRead = source.GetAnimeDetailAsync(42, CancellationToken.None);
            var replacement = Assert.IsType<Anime>(await replacementRead.WaitAsync(budget));
            Assert.Equal(42, replacement.ID);
            Assert.Equal("中文名", replacement.Title);
            var replacementJson = await cache.GetCacheAsync(key).WaitAsync(budget);
            Assert.NotNull(replacementJson);
            Assert.Equal("/v1/works/42", Assert.Single(handler.GetPaths));
            Assert.False(oldRead.IsCompleted);

            log.Resume();
            var restored = Assert.IsType<Anime>(await oldRead.WaitAsync(budget));

            Assert.Equal(JsonSerializer.Serialize(replacement, JsonOptions),
                JsonSerializer.Serialize(restored, JsonOptions));
            Assert.Equal("/v1/works/42", Assert.Single(handler.GetPaths));
            Assert.Equal(replacementJson, await cache.GetCacheAsync(key).WaitAsync(budget));
            var restartedCache = new CacheService(DbFactory);
            Assert.Equal(replacementJson, await restartedCache.GetCacheAsync(key).WaitAsync(budget));
            Assert.Equal(2, log.Captured.Entries.Count);
            Assert.All(log.Captured.Entries, entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.IsAssignableFrom<JsonException>(entry.Error);
                Assert.Contains("Removing invalid metadata cache", entry.Message);
            });
        }
#pragma warning disable CA1031 // 主断言失败时仍释放真实日志门闩，并保留排空失败信息。
        catch (Exception ex)
        {
            primaryFailure = ex;
            throw;
        }
#pragma warning restore CA1031
        finally
        {
            log.Resume();
            Task pending = replacementRead is null ? oldRead : Task.WhenAll(oldRead, replacementRead);
            await WaitForObservedCompletionAsync(pending, budget, primaryFailure);
        }
    }

    private sealed class PausedInvalidCacheWarning(TimeSpan budget) : ILogger<MetadataDataSource>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invalidWarnings;
        internal MetadataLogCapture Captured { get; } = new();
        internal Task Paused => _paused.Task;

        internal void Resume() => _release.Set();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Captured.Log(logLevel, eventId, state, exception, formatter);
            if (logLevel != LogLevel.Warning || exception is not JsonException
                || !formatter(state, exception).StartsWith("Removing invalid metadata cache", StringComparison.Ordinal)
                || Interlocked.Increment(ref _invalidWarnings) != 1)
            {
                return;
            }

            _paused.TrySetResult();
            if (!_release.Wait(budget))
                throw new TimeoutException("The test did not release the first invalid-cache warning.");
        }

        public void Dispose() => _release.Dispose();
    }
}
