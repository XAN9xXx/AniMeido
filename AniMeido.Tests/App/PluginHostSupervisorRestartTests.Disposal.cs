using AniMeido.PluginProtocol;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed partial class PluginHostSupervisorRestartTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationCallbackCanReenterDisposalAndDrainWithoutStartingAnotherCleanup(bool requestDrain)
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? reentered = null;
        session.NextStart = async token =>
        {
            using var registration = token.Register(() =>
                reentered = requestDrain ? supervisor.StopAndDrainAsync() : supervisor.DisposeAsync().AsTask());
            restartEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new PluginHostSnapshot([], [], [], []);
        };

        try
        {
            session.Exit(1);
            await restartEntered.Task.WaitAsync(Budget);
            var disposal = supervisor.DisposeAsync().AsTask();
            var drain = supervisor.StopAndDrainAsync();

            Assert.NotNull(reentered);
            Assert.Same(requestDrain ? drain : disposal, reentered);
            await Task.WhenAll(disposal, drain).WaitAsync(Budget);
            Assert.Equal(1, session.DisposeCount);
            Assert.False(supervisor.IsRunning);
            Assert.Empty(fixture.Logger.Warnings);
        }
        finally
        {
            await supervisor.StopAndDrainAsync().WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task DisposalBudgetDoesNotEndAnAlreadyEnteredSessionCleanup()
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.OnDispose = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };

        try
        {
            var disposal = supervisor.DisposeAsync().AsTask();
            await entered.Task.WaitAsync(Budget);
            await Task.Delay(TimeSpan.FromSeconds(6));

            Assert.False(disposal.IsCompleted);
            Assert.Empty(fixture.Logger.Warnings);
            release.TrySetResult();
            await disposal.WaitAsync(Budget);
            var warning = Assert.Single(fixture.Logger.Warnings);
            Assert.Equal(LogLevel.Warning, warning.Level);
            Assert.Null(warning.Exception);
            Assert.Equal("PluginHost supervisor exceeded the graceful disposal budget; forced cleanup completed.", warning.Message);
            Assert.Equal(1, session.DisposeCount);
        }
        finally
        {
            release.TrySetResult();
            await supervisor.StopAndDrainAsync().WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainStartedBeforePublicDisposalOwnsTheSameCleanup(bool failCleanup)
    {
        var fixture = await CreateAsync();
        var supervisor = fixture.Supervisor;
        var session = Assert.Single(fixture.Sessions);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new IOException("fixture drain-first cleanup failure");
        session.OnDispose = async () =>
        {
            entered.TrySetResult();
            await release.Task;
            if (failCleanup)
                throw failure;
        };
        var drain = supervisor.StopAndDrainAsync();
        Task disposal = Task.CompletedTask;

        try
        {
            await entered.Task.WaitAsync(Budget);
            disposal = supervisor.DisposeAsync().AsTask();
            Assert.Same(drain, supervisor.StopAndDrainAsync());
            Assert.False(drain.IsCompleted);
            Assert.False(disposal.IsCompleted);
            release.TrySetResult();

            if (failCleanup)
            {
                var drainFailure = await Assert.ThrowsAsync<AggregateException>(() => drain.WaitAsync(Budget));
                var disposeFailure = await Assert.ThrowsAsync<AggregateException>(() => disposal.WaitAsync(Budget));
                Assert.Same(failure, Assert.Single(drainFailure.InnerExceptions));
                Assert.Same(drainFailure, disposeFailure);
            }
            else
            {
                await Task.WhenAll(drain, disposal).WaitAsync(Budget);
            }
            Assert.Equal(1, session.DisposeCount);
            Assert.Empty(fixture.Logger.Warnings);
        }
        finally
        {
            release.TrySetResult();
            var completed = Task.WhenAll(drain, disposal);
            await Task.WhenAny(completed).WaitAsync(Budget);
            await completed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task CleanupAttemptsEverySessionAndPreservesEachFailure()
    {
        var fixture = await CreateAsync(includeSecondSession: true);
        var supervisor = fixture.Supervisor;
        var first = new IOException("fixture first cleanup failure");
        var second = new InvalidOperationException("fixture second cleanup failure");
        fixture.Sessions[0].OnDispose = () => Task.FromException(first);
        fixture.Sessions[1].OnDispose = () => Task.FromException(second);

        var failure = await Assert.ThrowsAsync<AggregateException>(() => supervisor.StopAndDrainAsync().WaitAsync(Budget));
        var repeated = await Assert.ThrowsAsync<AggregateException>(() => supervisor.StopAndDrainAsync().WaitAsync(Budget));

        Assert.Same(failure, repeated);
        Assert.Equal(2, failure.InnerExceptions.Count);
        Assert.Contains(first, failure.InnerExceptions);
        Assert.Contains(second, failure.InnerExceptions);
        Assert.All(fixture.Sessions, session => Assert.Equal(1, session.DisposeCount));
        Assert.False(supervisor.IsRunning);
        Assert.Empty(fixture.Logger.Warnings);
    }
}
