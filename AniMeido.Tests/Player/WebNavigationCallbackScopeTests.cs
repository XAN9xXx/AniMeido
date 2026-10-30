using AniMeido.Plugin.Player.Sources.Web;

namespace AniMeido.Tests;

public sealed class WebNavigationCallbackScopeTests
{
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Stop_WithNoCallbacksDrainsImmediately()
    {
        var scope = new WebNavigationCallbackScope();

        scope.StopAccepting();
        var drained = scope.WaitForDrainAsync();

        Assert.True(drained.IsCompletedSuccessfully);
        await drained.WaitAsync(TestBudget);
    }

    [Fact]
    public void Stop_RejectsNewCallbackRegistrations()
    {
        var scope = new WebNavigationCallbackScope();
        Assert.True(scope.IsAccepting);

        scope.StopAccepting();

        Assert.False(scope.IsAccepting);
        Assert.Null(scope.TryEnter());
    }

    [Fact]
    public async Task Stop_WaitsForTheAlreadyRunningCallbackToActuallyReturn()
    {
        var scope = new WebNavigationCallbackScope();
        var entered = NewSignal();
        var release = NewSignal();
        var callback = HoldCallbackAsync(scope, entered, release);
        await entered.Task.WaitAsync(TestBudget);

        scope.StopAccepting();
        var drained = scope.WaitForDrainAsync();
        try
        {
            Assert.False(scope.IsAccepting);
            Assert.Null(scope.TryEnter());
            Assert.False(callback.IsCompleted);
            Assert.False(drained.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await Task.WhenAll(callback, drained).WaitAsync(TestBudget);
    }

    [Fact]
    public async Task Stop_WaitsUntilEveryRegisteredCallbackHasReturned()
    {
        var scope = new WebNavigationCallbackScope();
        var firstEntered = NewSignal();
        var secondEntered = NewSignal();
        var firstRelease = NewSignal();
        var secondRelease = NewSignal();
        var first = HoldCallbackAsync(scope, firstEntered, firstRelease);
        var second = HoldCallbackAsync(scope, secondEntered, secondRelease);
        await Task.WhenAll(firstEntered.Task, secondEntered.Task).WaitAsync(TestBudget);

        scope.StopAccepting();
        var drained = scope.WaitForDrainAsync();
        try
        {
            firstRelease.SetResult();
            await first.WaitAsync(TestBudget);

            Assert.False(second.IsCompleted);
            Assert.False(drained.IsCompleted);
        }
        finally
        {
            firstRelease.TrySetResult();
            secondRelease.TrySetResult();
        }

        await Task.WhenAll(first, second, drained).WaitAsync(TestBudget);
    }

    [Fact]
    public async Task Stop_RepeatedStopAndRegistrationDisposalCannotDrainOtherCallbacks()
    {
        var scope = new WebNavigationCallbackScope();
        var first = scope.TryEnter();
        var second = scope.TryEnter();
        Assert.NotNull(first);
        Assert.NotNull(second);

        scope.StopAccepting();
        var drained = scope.WaitForDrainAsync();
        try
        {
            first.Dispose();
            first.Dispose();
            scope.StopAccepting();

            Assert.False(drained.IsCompleted);
            Assert.Null(scope.TryEnter());
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        await drained.WaitAsync(TestBudget);
    }

    [Fact]
    public async Task Stop_FaultedCallbackReturnsItsRegistrationThroughFinally()
    {
        var scope = new WebNavigationCallbackScope();
        var entered = NewSignal();
        var release = NewSignal();
        async Task FailCallbackAsync()
        {
            using var registration = scope.TryEnter();
            Assert.NotNull(registration);
            entered.SetResult();
            await release.Task;
            throw new InvalidOperationException("Callback fixture failure.");
        }
        var callback = FailCallbackAsync();
        await entered.Task.WaitAsync(TestBudget);

        scope.StopAccepting();
        var drained = scope.WaitForDrainAsync();
        try
        {
            Assert.False(drained.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => callback.WaitAsync(TestBudget));
        Assert.Equal("Callback fixture failure.", error.Message);
        await drained.WaitAsync(TestBudget);
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task HoldCallbackAsync(
        WebNavigationCallbackScope scope,
        TaskCompletionSource entered,
        TaskCompletionSource release)
    {
        using var registration = scope.TryEnter();
        Assert.NotNull(registration);
        entered.SetResult();
        await release.Task;
    }
}
