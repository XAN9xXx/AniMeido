using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PanelLoadStateTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task EnsureAsync_SynchronousSuccessPublishesCompletedState()
    {
        var state = new PanelLoadState();
        using var lifetime = new CancellationTokenSource();
        var calls = 0;

        Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            _ = cancellationToken;
            calls++;
            return Task.CompletedTask;
        }

        var first = state.EnsureAsync(Loader, lifetime.Token);
        Assert.NotNull(first);
        var second = state.EnsureAsync(Loader, lifetime.Token);
        Assert.NotNull(second);
        await first;
        await second;

        Assert.Equal(1, calls);
        Assert.True(state.IsLoaded);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public async Task EnsureAsync_SynchronousFailureCanRetry()
    {
        var state = new PanelLoadState();
        using var lifetime = new CancellationTokenSource();
        var calls = 0;

        Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            _ = cancellationToken;
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new InvalidOperationException("first load failed");
            }

            return Task.CompletedTask;
        }

        var first = state.EnsureAsync(Loader, lifetime.Token);
        Assert.NotNull(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        var retry = state.EnsureAsync(Loader, lifetime.Token);
        Assert.NotNull(retry);
        await retry;

        Assert.Equal(2, calls);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task EnsureAsync_SynchronousCancellationCanRetry()
    {
        var state = new PanelLoadState();
        using var cancelledLifetime = new CancellationTokenSource();
        cancelledLifetime.Cancel();
        using var retryLifetime = new CancellationTokenSource();
        var calls = 0;

        Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            return Task.CompletedTask;
        }

        var first = state.EnsureAsync(Loader, cancelledLifetime.Token);
        Assert.NotNull(first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var retry = state.EnsureAsync(Loader, retryLifetime.Token);
        Assert.NotNull(retry);
        await retry;

        Assert.Equal(2, calls);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task EnsureAsync_CoalescesConcurrentLoads()
    {
        var state = new PanelLoadState();
        using var lifetime = new CancellationTokenSource();
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            Interlocked.Increment(ref calls);
            return release.Task.WaitAsync(cancellationToken);
        }

        var first = state.EnsureAsync(Loader, lifetime.Token);
        Task second = Task.CompletedTask;
        try
        {
            second = state.EnsureAsync(Loader, lifetime.Token);
            Assert.Same(first, second);
            release.SetResult();
            await first.WaitAsync(Budget);
        }
        finally
        {
            release.TrySetResult();
            lifetime.Cancel();
            await ObserveLoadsAsync(first, second);
        }

        Assert.Equal(1, calls);
        Assert.True(state.IsLoaded);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public async Task InvalidateDuringLoad_IgnoresLateResultAndRetries()
    {
        var state = new PanelLoadState();
        using var lifetime = new CancellationTokenSource();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.SetResult();
                await firstRelease.Task;
                return;
            }

            await Task.Yield();
        }

        var first = state.EnsureAsync(Loader, lifetime.Token);
        Task retry = Task.CompletedTask;
        try
        {
            await firstStarted.Task.WaitAsync(Budget);
            state.Invalidate();
            retry = state.EnsureAsync(Loader, lifetime.Token);
            await retry.WaitAsync(Budget);
            firstRelease.SetResult();
            await first.WaitAsync(Budget);
        }
        finally
        {
            firstRelease.TrySetResult();
            lifetime.Cancel();
            await ObserveLoadsAsync(first, retry);
        }

        Assert.Equal(2, calls);
        Assert.True(state.IsLoaded);
        Assert.False(state.IsDirty);
    }

    [Fact]
    public async Task InvalidateCancelsRequestToken_AndAllowsRetry()
    {
        var state = new PanelLoadState();
        using var lifetime = new CancellationTokenSource();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return;
            }

            await Task.Yield();
        }

        var first = state.EnsureAsync(Loader, lifetime.Token);
        Task retry = Task.CompletedTask;
        try
        {
            await started.Task.WaitAsync(Budget);
            state.Invalidate();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Budget));

            retry = state.EnsureAsync(Loader, lifetime.Token);
            await retry.WaitAsync(Budget);
        }
        finally
        {
            lifetime.Cancel();
            await ObserveLoadsAsync(first, retry);
        }

        Assert.Equal(2, calls);
        Assert.True(state.IsLoaded);
    }

    [Fact]
    public async Task CancelInflight_AllowsLifecycleRetryWithoutLateCommit()
    {
        var state = new PanelLoadState();
        using var firstLifetime = new CancellationTokenSource();
        using var secondLifetime = new CancellationTokenSource();
        var firstRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task Loader(int generation, CancellationToken cancellationToken)
        {
            _ = generation;
            if (Interlocked.Increment(ref calls) == 1)
            {
                await firstRelease.Task;
                return;
            }

            await Task.Yield();
        }

        var first = state.EnsureAsync(Loader, firstLifetime.Token);
        Task retry = Task.CompletedTask;
        try
        {
            state.CancelInflight();
            retry = state.EnsureAsync(Loader, secondLifetime.Token);
            await retry.WaitAsync(Budget);
            firstRelease.SetResult();
            await first.WaitAsync(Budget);
        }
        finally
        {
            firstRelease.TrySetResult();
            firstLifetime.Cancel();
            secondLifetime.Cancel();
            await ObserveLoadsAsync(first, retry);
        }

        Assert.Equal(2, calls);
        Assert.True(state.IsLoaded);
        Assert.False(state.IsDirty);
    }

    private static async Task ObserveLoadsAsync(params Task[] loads)
    {
        var finished = Task.WhenAll(loads);
        await Task.WhenAny(finished).WaitAsync(Budget);
        await finished.ConfigureAwait(
            ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}
