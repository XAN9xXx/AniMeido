using AniMeido.Plugin.Player.Services;

namespace AniMeido.Tests;

public sealed class SourceManagementSessionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Stop_CancelsReadsButNotWrites_AndWaitsForEveryAcceptedOperation()
    {
        var session = new SourceManagementSession();
        var firstRead = session.TryBeginRead();
        var secondRead = session.TryBeginRead();
        var firstWrite = session.TryBeginWrite();
        var secondWrite = session.TryBeginWrite();
        Assert.NotNull(firstRead);
        Assert.NotNull(secondRead);
        Assert.NotNull(firstWrite);
        Assert.NotNull(secondWrite);
        var firstCancelled = NewGate();
        var secondCancelled = NewGate();
        using var firstRegistration = firstRead.Token.Register(() => firstCancelled.TrySetResult());
        using var secondRegistration = secondRead.Token.Register(() => secondCancelled.TrySetResult());
        Task? stopping = null;

        try
        {
            Assert.True(firstRead.Token.CanBeCanceled);
            Assert.Equal(CancellationToken.None, firstWrite.Token);
            Assert.Equal(CancellationToken.None, secondWrite.Token);
            stopping = session.StopAsync();
            await Task.WhenAll(firstCancelled.Task, secondCancelled.Task).WaitAsync(Budget);
            Assert.True(firstRead.Token.IsCancellationRequested);
            Assert.True(secondRead.Token.IsCancellationRequested);
            Assert.False(firstWrite.Token.IsCancellationRequested);
            Assert.False(secondWrite.Token.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.Null(session.TryBeginRead());
            Assert.Null(session.TryBeginWrite());
            Assert.Same(stopping, session.StopAsync());

            firstRead.Dispose();
            firstWrite.Dispose();
            Assert.False(stopping.IsCompleted);
            secondRead.Dispose();
            Assert.False(stopping.IsCompleted);
            secondWrite.Dispose();
            await stopping.WaitAsync(Budget);
            Assert.Same(stopping, session.StopAsync());
        }
        finally
        {
            firstRead.Dispose();
            secondRead.Dispose();
            firstWrite.Dispose();
            secondWrite.Dispose();
            await (stopping ?? session.StopAsync()).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Stop_WithoutOperationsIsRepeatable_AndPermanentlyRejectsNewWork()
    {
        var session = new SourceManagementSession();

        var stopping = session.StopAsync();
        await stopping.WaitAsync(Budget);

        Assert.Same(stopping, session.StopAsync());
        Assert.Null(session.TryBeginRead());
        Assert.Null(session.TryBeginWrite());
        Assert.False(session.HasSourceChanges);
    }

    [Fact]
    public async Task RepeatedDispose_DoesNotDrainAnotherAcceptedOperation()
    {
        var session = new SourceManagementSession();
        var first = session.TryBeginWrite();
        var second = session.TryBeginWrite();
        Assert.NotNull(first);
        Assert.NotNull(second);
        Task? stopping = null;
        try
        {
            stopping = session.StopAsync();
            first.Dispose();
            first.Dispose();
            Assert.False(stopping.IsCompleted);

            second.Dispose();
            await stopping.WaitAsync(Budget);
            second.Dispose();
            first.Dispose();
            Assert.Same(stopping, session.StopAsync());
        }
        finally
        {
            first.Dispose();
            second.Dispose();
            await (stopping ?? session.StopAsync()).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOperation_ReleasesItsLeaseThroughFinally(bool read)
    {
        var session = new SourceManagementSession();
        var started = NewGate();
        var release = NewGate();
        var failure = new InvalidOperationException("Synthetic source-management operation failure.");
        async Task FailAsync()
        {
            using var operation = read ? session.TryBeginRead() : session.TryBeginWrite();
            Assert.NotNull(operation);
            started.TrySetResult();
            await release.Task.WaitAsync(Budget);
            throw failure;
        }
        var pending = FailAsync();
        Task? stopping = null;
        try
        {
            await started.Task.WaitAsync(Budget);
            stopping = session.StopAsync();
            Assert.False(stopping.IsCompleted);
            release.TrySetResult();
            var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => pending.WaitAsync(Budget));
            Assert.Same(failure, actual);
            await stopping.WaitAsync(Budget);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await pending.WaitAsync(Budget);
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
            {
            }
            finally
            {
                await (stopping ?? session.StopAsync()).WaitAsync(Budget);
            }
        }
    }

    [Fact]
    public async Task FailedCancellationCallback_DoesNotFaultStopBeforeEveryLeaseFinishes()
    {
        var session = new SourceManagementSession();
        var read = session.TryBeginRead();
        var write = session.TryBeginWrite();
        Assert.NotNull(read);
        Assert.NotNull(write);
        var entered = NewGate();
        var failure = new InvalidOperationException("Synthetic cancellation callback failure.");
        using var registration = read.Token.Register(() =>
        {
            entered.TrySetResult();
            throw failure;
        });
        Task? stopping = null;
        try
        {
            stopping = session.StopAsync();
            await entered.Task.WaitAsync(Budget);
            Assert.False(stopping.IsCompleted);
            Assert.Same(stopping, session.StopAsync());
            read.Dispose();
            Assert.False(stopping.IsCompleted);
            write.Dispose();

            var error = await Record.ExceptionAsync(() => stopping.WaitAsync(Budget));
            Assert.NotNull(error);
            var original = error is AggregateException aggregate
                ? Assert.Single(aggregate.Flatten().InnerExceptions)
                : error;
            Assert.Same(failure, original);
            Assert.True(stopping.IsFaulted);
            Assert.Same(stopping, session.StopAsync());
        }
        finally
        {
            read.Dispose();
            write.Dispose();
            try
            {
                await (stopping ?? session.StopAsync()).WaitAsync(Budget);
            }
            catch (AggregateException ex) when (
                ex.InnerExceptions.Count == 1 && ReferenceEquals(ex.InnerExceptions[0], failure))
            {
            }
            catch (InvalidOperationException ex) when (ReferenceEquals(ex, failure))
            {
            }
        }
    }

    [Fact]
    public async Task SourceChanges_RemainTheSessionFact_WhenAnAcceptedWriteFinishesDuringStop()
    {
        var session = new SourceManagementSession();
        Assert.False(session.HasSourceChanges);
        var write = session.TryBeginWrite();
        Assert.NotNull(write);
        Task? stopping = null;
        try
        {
            stopping = session.StopAsync();
            Assert.False(stopping.IsCompleted);
            session.MarkSourcesChanged();
            session.MarkSourcesChanged();
            Assert.True(session.HasSourceChanges);

            write.Dispose();
            await stopping.WaitAsync(Budget);
            Assert.True(session.HasSourceChanges);
        }
        finally
        {
            write.Dispose();
            await (stopping ?? session.StopAsync()).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task CancellationNotification_CanObserveTheStoppedSession()
    {
        var session = new SourceManagementSession();
        var read = session.TryBeginRead();
        Assert.NotNull(read);
        var observed = new TaskCompletionSource<(bool ReadRejected, bool WriteRejected)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = read.Token.Register(() =>
        {
            using var lateRead = session.TryBeginRead();
            using var lateWrite = session.TryBeginWrite();
            observed.TrySetResult((lateRead is null, lateWrite is null));
        });
        Task? stopping = null;
        try
        {
            stopping = session.StopAsync();
            Assert.Equal((true, true), await observed.Task.WaitAsync(Budget));
            Assert.False(stopping.IsCompleted);
            read.Dispose();
            await stopping.WaitAsync(Budget);
        }
        finally
        {
            read.Dispose();
            await (stopping ?? session.StopAsync()).WaitAsync(Budget);
        }
    }

    private static TaskCompletionSource NewGate()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
