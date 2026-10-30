using AniMeido.App.Services;
using AniMeido.Contracts.Desktop;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class GlobalShortcutLifetimeTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Dispose_DuringInitializationWaitsAndPreventsLateNativeListener()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedToken = default;
        var action = new ShortcutAction(async token =>
        {
            observedToken = token;
            entered.SetResult();
            await finish.Task;
        });
        await using var manager = new GlobalShortcutManager([action],
            NullLogger<GlobalShortcutManager>.Instance);
        var startup = manager.StartAsync();
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(Budget);
            stopping = manager.DisposeAsync().AsTask();
            Assert.True(observedToken.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.Same(stopping, manager.DisposeAsync().AsTask());
            finish.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(Budget));
            await stopping.WaitAsync(Budget);
            Assert.Null(typeof(GlobalShortcutManager)
                .GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager));
        }
        finally
        {
            finish.TrySetResult();
            try
            {
                await startup.WaitAsync(Budget);
            }
            catch (OperationCanceledException)
            {
            }
            if (stopping is not null)
            {
                await stopping.WaitAsync(Budget);
            }
        }
    }

    [Fact]
    public async Task Dispose_WhenInitializationCancellationCallbackFails_StillDrainsAndReportsTheFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new InvalidOperationException("shortcut cancellation callback failed");
        CancellationToken observedToken = default;
        var action = new ShortcutAction(async token =>
        {
            observedToken = token;
            using var registration = token.Register(() => throw error);
            entered.SetResult();
            await finish.Task;
        });
        var manager = new GlobalShortcutManager([action],
            NullLogger<GlobalShortcutManager>.Instance);
        var startup = manager.StartAsync();
        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(Budget);
            stopping = manager.DisposeAsync().AsTask();

            Assert.True(observedToken.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.Same(stopping, manager.DisposeAsync().AsTask());
            finish.SetResult();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup.WaitAsync(Budget));
            var failure = await Assert.ThrowsAsync<AggregateException>(() => stopping.WaitAsync(Budget));
            Assert.Contains(error, failure.Flatten().InnerExceptions);
            Assert.True(stopping.IsFaulted);
            Assert.Same(stopping, manager.DisposeAsync().AsTask());
            Assert.Null(typeof(GlobalShortcutManager)
                .GetField("_thread", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(manager));
        }
        finally
        {
            finish.TrySetResult();
            try
            {
                await ObserveShortcutStopTaskAsync(startup).WaitAsync(Budget);
            }
            finally
            {
                if (stopping is not null)
                    await ObserveShortcutStopTaskAsync(stopping).WaitAsync(Budget);
            }
        }
    }

    [Fact]
    public async Task EmptyManager_DisposalIsIdempotentAndRejectsNewStartup()
    {
        var manager = new GlobalShortcutManager([], NullLogger<GlobalShortcutManager>.Instance);
        await manager.StartAsync();
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.StartAsync());
    }

    private static async Task ObserveShortcutStopTaskAsync(Task task)
        => await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

    private sealed class ShortcutAction(Func<CancellationToken, Task> initialize) : IGlobalShortcutAction
    {
        public string Id => "fixture";
        public int VirtualKey => 0;
        public bool IsEnabled => false;
        public bool SuppressInput => false;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => initialize(cancellationToken);
        public Task ExecuteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
