using System.Reflection;

namespace AniMeido.Tests;

[Trait("Category", "Mechanism")]
public sealed class HostUiDispatcherTests
{
    [Fact]
    public async Task RejectedQueueFailsWithoutRunningTheAction()
    {
        var run = CreateDispatcher(() => false, _ => false);
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => run(() =>
        {
            calls++;
            return Task.CompletedTask;
        }));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task AcceptedQueueWaitsForTheActualWork()
    {
        Action? queued = null;
        var run = CreateDispatcher(() => false, action =>
        {
            queued = action;
            return true;
        });
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = run(async () =>
        {
            started.TrySetResult();
            await release.Task;
        });
        try
        {
            Assert.False(operation.IsCompleted);
            var invoke = queued;
            Assert.NotNull(invoke);
            queued = null;
            invoke();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(operation.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            queued?.Invoke();
            await Task.WhenAny(operation).WaitAsync(TimeSpan.FromSeconds(5));
            await operation.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        await operation.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedFailurePreservesTheOriginalException(bool throwsSynchronously)
    {
        var run = CreateDispatcher(() => false, action =>
        {
            action();
            return true;
        });
        var original = new InvalidDataException("synthetic failure");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => run(() =>
            throwsSynchronously ? throw original : Task.FromException(original)));
        Assert.Same(original, error);
    }

    [Fact]
    public async Task CurrentUiThreadDoesNotEnqueueAgain()
    {
        var run = CreateDispatcher(() => true, _ => throw new InvalidOperationException("unexpected queue"));
        var calls = 0;
        await run(() =>
        {
            calls++;
            return Task.CompletedTask;
        });
        Assert.Equal(1, calls);
    }

    private static Func<Func<Task>, Task> CreateDispatcher(Func<bool> hasAccess, Func<Action, bool> enqueue)
    {
        var assembly = Assembly.LoadFrom(AppDataRootsTests.BuildOutput("AniMeido.PluginHost"));
        var type = assembly.GetType("AniMeido.PluginHost.HostUiDispatcher");
        Assert.NotNull(type);
        var instance = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [hasAccess, enqueue], culture: null);
        var method = type.GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.Public)!;
        return action => (Task)method.Invoke(instance, [action])!;
    }
}
