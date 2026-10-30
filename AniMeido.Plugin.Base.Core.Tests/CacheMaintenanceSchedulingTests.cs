using AniMeido.Plugin.Base.Services;
using System.Collections;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class CacheMaintenanceSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task CleanExpired_ReturnsWhileSqliteIsLocked_AndFinishesWithoutResumingTheCallingContext()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("expired", "old", TimeSpan.FromDays(-31));
        await cache.SetCacheAsync("retained", "offline", TimeSpan.FromDays(-1));
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(cache.CleanExpiredAsync, context);
        Task operation;
        try
        {
            operation = await returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            blocker.Dispose();
            await operation.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            var pending = await returned.WaitAsync(CompletionBudget);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        Assert.Equal(0L, await RowCountAsync("expired"));
        Assert.Equal(1L, await RowCountAsync("retained"));
        Assert.Equal("offline", await cache.GetCacheAllowExpiredAsync("retained"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CleanExpired_QueuesBehindAnAlreadyRegisteredFreshWrite_AndKeepsItsNewValue()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "expired", TimeSpan.FromDays(-31));
        var gate = (SemaphoreSlim)typeof(CacheService)
            .GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        await gate.WaitAsync();
        Task? saving = null;
        Task? cleaning = null;
        try
        {
            saving = cache.SetCacheAsync("fixture", "fresh", TimeSpan.FromDays(1));
            cleaning = cache.CleanExpiredAsync();

            Assert.False(saving.IsCompleted);
            Assert.False(cleaning.IsCompleted);
        }
        finally
        {
            gate.Release();
            var pending = Task.WhenAll(saving ?? Task.CompletedTask, cleaning ?? Task.CompletedTask);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await saving;
        await cleaning;
        Assert.Equal("fresh", await cache.GetCacheAsync("fixture"));
        Assert.Equal("fresh", await new CacheService(Factory).GetCacheAsync("fixture"));
    }

    [Theory]
    [InlineData(-31, false)]
    [InlineData(-29, true)]
    [Trait("Category", "Mechanism")]
    public async Task CleanExpired_UsesTheSameThirtyDayRetentionForSqliteAndHotMemory(int expiresInDays, bool retained)
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "cached-data", TimeSpan.FromDays(expiresInDays));
        Assert.Equal(1L, await RowCountAsync("fixture"));

        await cache.CleanExpiredAsync();

        Assert.Equal(retained ? 1L : 0L, await RowCountAsync("fixture"));
        Assert.Equal(retained, GetMemory(cache).Contains("fixture"));
        Assert.Null(await cache.GetCacheAsync("fixture"));
        Assert.Equal(retained ? "cached-data" : null, await cache.GetCacheAllowExpiredAsync("fixture"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CleanExpired_ImportBusyLeavesSqliteAndMemoryUntouched()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "expired", TimeSpan.FromDays(-31));
        using var import = Factory.Access.BeginExclusive();
        await import.WaitForDrainAsync(CancellationToken.None);
        Task cleaning;
        using (ExecutionContext.SuppressFlow())
        {
            // 模拟导入外的后台维护，不能继承导入自己的嵌套写入资格。
            cleaning = Task.Run(cache.CleanExpiredAsync);
        }

        await cleaning.WaitAsync(CompletionBudget);

        Assert.Equal(1L, await RowCountAsync("fixture"));
        Assert.True(GetMemory(cache).Contains("fixture"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task CleanExpired_SqlFailureKeepsObservedData_AndDoesNotBlockLaterMutationOrRetry()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("expired", "old", TimeSpan.FromDays(-31));
        await ExecuteAsync("""
            CREATE TRIGGER reject_fixture_cleanup BEFORE DELETE ON cache
            WHEN OLD.CacheKey='expired'
            BEGIN SELECT RAISE(ABORT, 'fixture cleanup failure'); END;
            """);

        await cache.CleanExpiredAsync().WaitAsync(CompletionBudget);

        Assert.Equal(1L, await RowCountAsync("expired"));
        Assert.True(GetMemory(cache).Contains("expired"));
        await cache.SetCacheAsync("fresh", "new", TimeSpan.FromDays(1)).WaitAsync(CompletionBudget);
        Assert.Equal("new", await cache.GetCacheAsync("fresh"));

        await ExecuteAsync("DROP TRIGGER reject_fixture_cleanup;");
        await cache.CleanExpiredAsync().WaitAsync(CompletionBudget);
        Assert.Equal(0L, await RowCountAsync("expired"));
        Assert.False(GetMemory(cache).Contains("expired"));
        Assert.Equal("new", await cache.GetCacheAsync("fresh"));
    }

    private async Task<long> RowCountAsync(string key)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cache WHERE CacheKey=@key";
        command.Parameters.AddWithValue("@key", key);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static IDictionary GetMemory(CacheService cache)
        => (IDictionary)typeof(CacheService).GetField("_memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;
}
