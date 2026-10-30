using AniMeido.Plugin.Base.Services;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class CacheReadSchedulingTests : IAsyncLifetime
{
    private static readonly TimeSpan ReturnBudget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CompletionBudget = TimeSpan.FromSeconds(5);
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(false, 24, true)]
    [InlineData(false, -24, false)]
    [InlineData(true, 24, true)]
    [InlineData(true, -24, true)]
    [InlineData(true, -744, false)]
    public async Task ColdRead_ReturnsWhileSqliteIsLocked_AndPreservesValidityAndRetention(
        bool allowExpired, int expiresInHours, bool retained)
    {
        await new CacheService(Factory).SetCacheAsync("fixture", "cached-data", TimeSpan.FromHours(expiresInHours));
        var cache = new CacheService(Factory);
        using var blocker = await _database.LockExclusiveAsync();
        var context = new PausedUiContext();
        var invocation = CallFromPausedContextThread(() => Read(cache, "fixture", allowExpired), context);
        Task<string?> operation;
        try
        {
            operation = await invocation.Returned.WaitAsync(ReturnBudget);
            Assert.False(operation.IsCompleted);
            blocker.Dispose();
            await operation.WaitAsync(CompletionBudget);
            Assert.Equal(0, context.PostCount);
        }
        finally
        {
            blocker.Dispose();
            context.Resume();
            await FinishInvocationAsync(invocation);
        }

        Assert.Equal(retained ? "cached-data" : null, await operation);
    }

    [Theory]
    [InlineData(false, 24)]
    [InlineData(true, 24)]
    [InlineData(true, -24)]
    public async Task HotRead_CompletesSynchronouslyEvenWhileSqliteIsLocked(bool allowExpired, int expiresInHours)
    {
        var cache = new CacheService(Factory);
        await cache.SetCacheAsync("fixture", "hot-data", TimeSpan.FromHours(expiresInHours));
        using var blocker = await _database.LockExclusiveAsync();

        var operation = Read(cache, "fixture", allowExpired);
        try
        {
            Assert.True(operation.IsCompletedSuccessfully);
            Assert.Equal("hot-data", await operation);
        }
        finally
        {
            blocker.Dispose();
            await Task.WhenAny(operation).WaitAsync(CompletionBudget);
            await ((Task)operation).ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public enum CacheMutationKind { Set, Remove, Clear }

    [Theory]
    [InlineData(CacheMutationKind.Set)]
    [InlineData(CacheMutationKind.Remove)]
    [InlineData(CacheMutationKind.Clear)]
    [Trait("Category", "Mechanism")]
    public async Task ColdRead_QueuesBehindEarlierMutation_WithoutOverwritingOrRevivingOldData(CacheMutationKind kind)
    {
        await new CacheService(Factory).SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        var cache = new CacheService(Factory);
        var gate = GetMutationGate(cache);
        await gate.WaitAsync();
        Task? mutation = null;
        Task<string?>? read = null;
        try
        {
            mutation = kind switch
            {
                CacheMutationKind.Set => cache.SetCacheAsync("fixture", "new", TimeSpan.FromDays(1)),
                CacheMutationKind.Remove => cache.RemoveCacheAsync("fixture"),
                CacheMutationKind.Clear => cache.ClearAllCacheAsync(),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            read = cache.GetCacheAsync("fixture");

            Assert.False(read.IsCompleted);
        }
        finally
        {
            gate.Release();
            var pending = Task.WhenAll(mutation ?? Task.CompletedTask, (Task?)read ?? Task.CompletedTask);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        await mutation;
        var expected = kind == CacheMutationKind.Set ? "new" : null;
        Assert.Equal(expected, await read);
        Assert.Equal(expected, await cache.GetCacheAsync("fixture"));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task ColdRead_CapturesGenerationBeforeQueueing_AndRejectsDataWrittenAfterClear()
    {
        await new CacheService(Factory).SetCacheAsync("fixture", "old", TimeSpan.FromDays(1));
        var cache = new CacheService(Factory);
        var gate = GetMutationGate(cache);
        await gate.WaitAsync();
        var originalHeld = true;
        var afterClearEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePublication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? afterClearHold = null;
        Task? clearing = null;
        Task<string?>? read = null;
        try
        {
            clearing = cache.ClearAllCacheAsync();
            afterClearHold = HoldAfterClearAsync();
            read = cache.GetCacheAsync("fixture");
            Assert.False(read.IsCompleted);

            gate.Release();
            originalHeld = false;
            await afterClearEntered.Task.WaitAsync(CompletionBudget);
            await clearing.WaitAsync(CompletionBudget);

            // 另一实例代表清空完成后的新写入；原请求不能因此变成新代次请求。
            await new CacheService(Factory).SetCacheAsync("fixture", "after-clear", TimeSpan.FromDays(1));
            releasePublication.TrySetResult();
            await afterClearHold.WaitAsync(CompletionBudget);

            Assert.Null(await read.WaitAsync(CompletionBudget));
            Assert.Equal("after-clear", await cache.GetCacheAsync("fixture"));
        }
        finally
        {
            if (originalHeld)
            {
                gate.Release();
            }
            releasePublication.TrySetResult();
            var pending = Task.WhenAll(clearing ?? Task.CompletedTask,
                afterClearHold ?? Task.CompletedTask, (Task?)read ?? Task.CompletedTask);
            await Task.WhenAny(pending).WaitAsync(CompletionBudget);
            await pending.ConfigureAwait(
                ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        async Task HoldAfterClearAsync()
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                afterClearEntered.TrySetResult();
                await releasePublication.Task.ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static Task<string?> Read(CacheService cache, string key, bool allowExpired)
        => allowExpired ? cache.GetCacheAllowExpiredAsync(key) : cache.GetCacheAsync(key);

    private static SemaphoreSlim GetMutationGate(CacheService cache)
        => (SemaphoreSlim)typeof(CacheService).GetField("_mutationGate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;

    private static ContextInvocation CallFromPausedContextThread(
        Func<Task<string?>> read, SynchronizationContext context)
    {
        var returned = new TaskCompletionSource<Task<string?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                returned.SetResult(read());
            }
#pragma warning disable CA1031 // 专用调用线程把原始失败交还测试，不允许未观察异常退出进程。
            catch (Exception error)
            {
                returned.SetException(error);
            }
#pragma warning restore CA1031
            finally
            {
                SynchronizationContext.SetSynchronizationContext(null);
                exited.SetResult();
            }
        }) { IsBackground = true };
        thread.Start();
        return new ContextInvocation(returned.Task, exited.Task);
    }

    private static async Task FinishInvocationAsync(ContextInvocation invocation)
    {
        var operation = await invocation.Returned.WaitAsync(CompletionBudget);
        await Task.WhenAny(operation).WaitAsync(CompletionBudget);
        await ((Task)operation).ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        await invocation.Exited.WaitAsync(CompletionBudget);
    }

    private sealed record ContextInvocation(Task<Task<string?>> Returned, Task Exited);
}
