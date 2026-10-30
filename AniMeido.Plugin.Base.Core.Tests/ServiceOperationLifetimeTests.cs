using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.Core.Tests;

public sealed class ServiceOperationLifetimeTests
{
    [Fact]
    public async Task StopWaitsForEveryOperationAndReleasesOnce()
    {
        var lifetime = new ServiceOperationLifetime();
        using var first = lifetime.Begin();
        using var second = lifetime.Begin();
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstRegistration = first.Token.Register(() => firstCancelled.TrySetResult());
        using var secondRegistration = second.Token.Register(() => secondCancelled.TrySetResult());
        var releases = 0;

        var stopping = lifetime.StopAsync(() => releases++).AsTask();
        Assert.Same(stopping, lifetime.StopAsync(() => releases++).AsTask());
        await Task.WhenAll(firstCancelled.Task, secondCancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(first.Token.IsCancellationRequested);
        Assert.True(second.Token.IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => lifetime.Begin());
        first.Dispose();
        first.Dispose();
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, releases);

        second.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task AlreadyCancelledRequestDoesNotHoldShutdownOpen()
    {
        var lifetime = new ServiceOperationLifetime();
        Assert.Throws<OperationCanceledException>(() => lifetime.Begin(new CancellationToken(true)));

        var released = false;
        await lifetime.StopAsync(() => released = true).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(released);
    }

    [Fact]
    public async Task CancellationCallbackFailureStillWaitsForOperationBeforeRelease()
    {
        var lifetime = new ServiceOperationLifetime();
        using var operation = lifetime.Begin();
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = operation.Token.Register(() =>
        {
            callbackStarted.TrySetResult();
            throw new InvalidOperationException("Synthetic callback failure");
        });
        var released = false;

        var stopping = lifetime.StopAsync(() => released = true).AsTask();
        await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(stopping.IsCompleted);
        Assert.False(released);
        operation.Dispose();

        await Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(released);
    }
}
