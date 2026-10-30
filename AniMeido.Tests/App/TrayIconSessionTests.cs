using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class TrayIconSessionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartupFailureFaultsReadyWithTheOriginalCauseWithoutRuntimeNotification()
    {
        var unavailable = 0;
        var session = new TrayIconSession(() => { }, () => { }, () => unavailable++);
        var failure = new InvalidOperationException("Synthetic native startup failure.");

        session.RecordFailure(failure);
        Assert.False(session.Complete());
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => session.Ready.WaitAsync(Budget));
        session.NotifyUnavailable();

        Assert.Same(failure, actual.InnerException);
        Assert.Same(failure, session.Failure);
        Assert.True(session.Stopped.IsCompletedSuccessfully);
        Assert.False(session.IsRunning);
        Assert.Equal(0, unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedRuntimeCompletionNotifiesOnceAfterStopped(bool failure)
    {
        var unavailable = 0;
        TrayIconSession? session = null;
        session = new TrayIconSession(() => { }, () => { }, () =>
        {
            Assert.True(session!.Stopped.IsCompletedSuccessfully);
            unavailable++;
        });
        session.MarkReady();
        await session.Ready.WaitAsync(Budget);
        Assert.True(session.IsRunning);
        if (failure)
        {
            session.RecordFailure(new InvalidOperationException("Synthetic message-loop failure."));
            Assert.False(session.IsRunning);
        }

        Assert.True(session.Complete());
        session.NotifyUnavailable();
        session.NotifyUnavailable();

        Assert.True(session.Stopped.IsCompletedSuccessfully);
        Assert.False(session.IsRunning);
        Assert.Equal(1, unavailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestedStopRejectsActionsAndWaitsForActualCompletion(bool ready)
    {
        var calls = 0;
        var session = new TrayIconSession(() => calls++, () => calls++, () => calls++);
        if (ready)
        {
            session.MarkReady();
            await session.Ready.WaitAsync(Budget);
        }

        session.RequestStop();
        session.InvokeOpen();
        session.InvokeExit();
        session.NotifyUnavailable();

        Assert.True(session.StopRequested);
        Assert.False(session.IsRunning);
        Assert.False(session.Stopped.IsCompleted);
        Assert.Equal(0, calls);
        Assert.False(session.Complete());
        await session.Stopped.WaitAsync(Budget);
        session.NotifyUnavailable();
        Assert.Equal(0, calls);
        if (!ready)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Ready.WaitAsync(Budget));
        }
    }

    [Fact]
    public void FirstFailureIsRetainedAndImmediatelyPreventsRunningWork()
    {
        var session = new TrayIconSession(() => Assert.Fail("Failed session cannot open."),
            () => Assert.Fail("Failed session cannot exit."), () => { });
        session.MarkReady();
        var first = new InvalidOperationException("First native failure.");

        session.RecordFailure(first);
        session.RecordFailure(new InvalidOperationException("Later cleanup failure."));
        session.InvokeOpen();
        session.InvokeExit();

        Assert.Same(first, session.Failure);
        Assert.False(session.IsRunning);
        Assert.True(session.Complete());
    }

    [Fact]
    public void QueuedOldSessionActionsCannotReviveWhenAnotherSessionStarts()
    {
        var oldCalls = 0;
        var currentCalls = 0;
        var old = new TrayIconSession(() => oldCalls++, () => oldCalls++, () => { });
        old.MarkReady();
        Action queuedOpen = old.InvokeOpen;
        Action queuedExit = old.InvokeExit;
        old.RequestStop();
        Assert.False(old.Complete());
        var current = new TrayIconSession(() => currentCalls++, () => currentCalls++, () => { });
        current.MarkReady();

        queuedOpen();
        queuedExit();
        current.InvokeOpen();
        current.InvokeExit();

        Assert.Equal(0, oldCalls);
        Assert.Equal(2, currentCalls);
        Assert.True(current.IsRunning);
        current.RequestStop();
        Assert.False(current.Complete());
    }

    [Fact]
    public void RequestedStopSuppressesTheAlreadyQueuedRuntimeNotification()
    {
        var unavailable = 0;
        var session = new TrayIconSession(() => { }, () => { }, () => unavailable++);
        session.MarkReady();
        Assert.True(session.Complete());
        Action queued = session.NotifyUnavailable;

        session.RequestStop();
        queued();

        Assert.Equal(0, unavailable);
        Assert.True(session.Stopped.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("exit")]
    [InlineData("unavailable")]
    public async Task ActionsRunOutsideTheSessionLock(string action)
    {
        TrayIconSession? session = null;
        Task? stopping = null;
        var called = false;
        using var stopReturned = new ManualResetEventSlim();
        void ReenterFromAnotherThread()
        {
            stopping = Task.Run(() =>
            {
                session!.RequestStop();
                stopReturned.Set();
            });
            Assert.True(stopReturned.Wait(Budget), "Session actions must not hold the state lock.");
            called = true;
        }

        session = new TrayIconSession(ReenterFromAnotherThread, ReenterFromAnotherThread, ReenterFromAnotherThread);
        session.MarkReady();
        try
        {
            switch (action)
            {
                case "open":
                    session.InvokeOpen();
                    break;
                case "exit":
                    session.InvokeExit();
                    break;
                default:
                    Assert.True(session.Complete());
                    session.NotifyUnavailable();
                    break;
            }

            Assert.True(called);
            Assert.True(session.StopRequested);
        }
        finally
        {
            if (stopping is not null)
            {
                await stopping.WaitAsync(Budget);
            }
            session.RequestStop();
            session.Complete();
        }
    }
}
