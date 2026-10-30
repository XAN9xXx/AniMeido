using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class CacheClearAndStatsSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private const double ImageSizeKB = 17.25;
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData("clear")]
    [InlineData("metadata")]
    [InlineData("stats")]
    public async Task CacheOperation_ReturnsWhileSqliteIsLocked_AndCompletesWithoutTheCallingContext(string operation)
    {
        var imageClears = 0;
        var cache = new CacheService(_database.Factory,
            () => Interlocked.Increment(ref imageClears), () => ImageSizeKB);
        await SeedCacheAsync(cache);
        using var blocker = await _database.LockExclusiveAsync();
        Task<(int count, double sizeKB)>? stats = null;
        var invocation = InvokeCacheOperation(() => operation switch
        {
            "clear" => cache.ClearAllCacheAsync(),
            "metadata" => cache.ClearMetadataForIdRemapAsync(),
            "stats" => stats = cache.GetCacheStatsAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
        try
        {
            var pending = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(pending.IsCompleted);
            Assert.Equal(0, imageClears);
            blocker.Dispose();
            await pending.WaitAsync(CompletionBudget);
        }
        finally
        {
            blocker.Dispose();
            invocation.Context.Resume();
            await FinishCacheOperationAsync(invocation.Returned);
        }

        if (operation == "stats")
        {
            var measured = await stats!;
            Assert.Equal(2, measured.count);
            // 原指标按 SQLite LENGTH 统计字符数，不改成 UTF-8 字节数。
            Assert.Equal(5 / 1024.0 + ImageSizeKB, measured.sizeKB);
            Assert.Equal("abc", await cache.GetCacheAsync("ascii"));
            Assert.Equal("中文", await cache.GetCacheAsync("unicode"));
            Assert.Equal(0, imageClears);
        }
        else
        {
            Assert.Null(await cache.GetCacheAsync("ascii"));
            Assert.Null(await cache.GetCacheAllowExpiredAsync("unicode"));
            Assert.Equal(operation == "clear" ? 1 : 0, imageClears);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Mechanism")]
    public async Task FailedClear_KeepsGenerationAndMemoryInvalidationButDoesNotClearImages(bool metadataOnly)
    {
        var imageClears = 0;
        var cache = new CacheService(_database.Factory, () => Interlocked.Increment(ref imageClears));
        await cache.SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        Assert.Equal("old", await cache.GetCacheAsync("fixture"));
        var generation = cache.CaptureGeneration();
        using (var connection = await _database.Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_fixture_cache_clear BEFORE DELETE ON cache BEGIN
                    SELECT RAISE(ABORT, 'fixture cache clear failure');
                END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        var failure = await Assert.ThrowsAsync<SqliteException>(() =>
            metadataOnly ? cache.ClearMetadataForIdRemapAsync() : cache.ClearAllCacheAsync());

        Assert.Equal(19, failure.SqliteErrorCode);
        Assert.Equal(generation + 1, cache.CaptureGeneration());
        Assert.Equal(0, imageClears);
        using (var connection = await _database.Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 删除失败后的磁盘记录仍在；改值后读到新内容，证明旧热缓存确已失效。
            command.CommandText = "UPDATE cache SET Data='disk-after-failure' WHERE CacheKey='fixture'";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.Equal("disk-after-failure", await cache.GetCacheAsync("fixture"));
        await _database.Factory.StopAsync().WaitAsync(CompletionBudget);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CacheCallback_RemainsOwnedUntilItsRealReturnBeforeDatabaseStopCompletes(bool stats)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var callbackCalls = 0;
        void BlockCallback()
        {
            Interlocked.Increment(ref callbackCalls);
            entered.TrySetResult();
            release.Wait();
        }
        var cache = new CacheService(_database.Factory,
            () => BlockCallback(),
            () =>
            {
                BlockCallback();
                return ImageSizeKB;
            });
        await SeedCacheAsync(cache);
        Task<(int count, double sizeKB)>? measured = null;
        var invocation = InvokeCacheOperation(() =>
            stats ? measured = cache.GetCacheStatsAsync() : cache.ClearAllCacheAsync());
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(CompletionBudget);
            var pending = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(pending.IsCompleted);
            Assert.Equal(1, callbackCalls);
            stopping = _database.Factory.StopAsync();
            Assert.False(stopping.IsCompleted);

            release.Set();
            await pending.WaitAsync(CompletionBudget);
            await stopping.WaitAsync(CompletionBudget);
        }
        finally
        {
            release.Set();
            invocation.Context.Resume();
            await FinishCacheOperationAsync(invocation.Returned);
            if (stopping is not null)
                await stopping.WaitAsync(CompletionBudget);
        }

        Assert.Equal(1, callbackCalls);
        if (stats)
        {
            var result = await measured!;
            Assert.Equal(2, result.count);
            Assert.Equal(5 / 1024.0 + ImageSizeKB, result.sizeKB);
        }
        else
        {
            using var connection = _database.Factory.CreateConnection();
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM cache";
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }
    }

    private static async Task SeedCacheAsync(CacheService cache)
    {
        await cache.SetCacheAsync("ascii", "abc", TimeSpan.FromDays(1));
        await cache.SetCacheAsync("unicode", "中文", TimeSpan.FromDays(1));
    }

    private static (Task<Task> Returned, PausedUiContext Context) InvokeCacheOperation(Func<Task> operation)
    {
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync(() => operation(), context);
        return (returned, context);
    }

    private static async Task FinishCacheOperationAsync(Task<Task> returned)
    {
        var operation = await returned.WaitAsync(CompletionBudget);
        await operation.WaitAsync(CompletionBudget);
    }
}
