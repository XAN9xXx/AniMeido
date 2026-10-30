using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Collections;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class CacheMutationSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_ReturnsWhileSqliteIsLocked_AndFinishesWithoutThePausedCallingContext(bool remove)
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var returned = SchedulingTestThread.InvokeAsync<Task>(() => remove
            ? cache.RemoveCacheAsync("fixture")
            : cache.SetCacheAsync("fixture", "new", TimeSpan.FromDays(1)), context);
        Task operation;
        try
        {
            operation = await returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            if (!remove)
            {
                var hot = cache.GetCacheAsync("fixture");
                Assert.True(hot.IsCompletedSuccessfully);
                Assert.Equal("old", await hot);
            }

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

        Assert.Equal(remove ? null : "new", await cache.GetCacheAsync("fixture"));
    }

    [Fact]
    public async Task SetCache_SqlFailureDoesNotPublishRejectedData_AndReleasesTheMutationGate()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        await ExecuteAsync("""
            CREATE TRIGGER reject_fixture_cache BEFORE INSERT ON cache
            WHEN NEW.CacheKey='fixture' AND NEW.Data='rejected'
            BEGIN SELECT RAISE(ABORT, 'fixture cache write failure'); END;
            """);

        var failure = await Assert.ThrowsAsync<SqliteException>(
            () => cache.SetCacheAsync("fixture", "rejected", TimeSpan.FromDays(1)));

        Assert.Contains("fixture cache write failure", failure.Message);
        var hot = cache.GetCacheAsync("fixture");
        Assert.True(hot.IsCompletedSuccessfully);
        Assert.Equal("old", await hot);
        Assert.Equal("old", await new CacheService(Factory).GetCacheAsync("fixture"));

        await cache.SetCacheAsync("fixture", "accepted", TimeSpan.FromDays(1)).WaitAsync(CompletionBudget);
        Assert.Equal("accepted", await cache.GetCacheAsync("fixture"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task RemoveCache_SqlFailureStillInvalidatesHotData_AndReleasesTheMutationGate()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        await ExecuteAsync("""
            CREATE TRIGGER reject_fixture_delete BEFORE DELETE ON cache
            WHEN OLD.CacheKey='fixture'
            BEGIN SELECT RAISE(ABORT, 'fixture cache removal failure'); END;
            """);

        var failure = await Assert.ThrowsAsync<SqliteException>(() => cache.RemoveCacheAsync("fixture"));

        Assert.Contains("fixture cache removal failure", failure.Message);
        var memory = (IDictionary)typeof(CacheService)
            .GetField("_memoryCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        Assert.False(memory.Contains("fixture"));
        Assert.Equal("old", await new CacheService(Factory).GetCacheAsync("fixture"));

        await cache.SetCacheAsync("other", "fresh", TimeSpan.FromDays(1)).WaitAsync(CompletionBudget);
        Assert.Equal("fresh", await cache.GetCacheAsync("other"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task SetCache_QueuedBeforeClearFinishesCannotRecaptureTheNewGenerationAndRefillIt()
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("existing", "old", TimeSpan.FromDays(1));
        var gate = (SemaphoreSlim)typeof(CacheService)
            .GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cache)!;
        await gate.WaitAsync();
        Task? clearing = null;
        Task? saving = null;
        try
        {
            clearing = cache.ClearAllCacheAsync();
            saving = cache.SetCacheAsync("late", "stale", TimeSpan.FromDays(1));
            Assert.False(clearing.IsCompleted);
            Assert.False(saving.IsCompleted);
        }
        finally
        {
            gate.Release();
            var pending = Task.WhenAll(clearing ?? Task.CompletedTask, saving ?? Task.CompletedTask);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await clearing;
        await saving;
        Assert.Null(await cache.GetCacheAsync("late"));
        Assert.Null(await new CacheService(Factory).GetCacheAllowExpiredAsync("late"));
    }

    private async Task ExecuteAsync(string sql)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

}
