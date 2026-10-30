using AniMeido.App.Services;
using System.Runtime.InteropServices;
using AniMeido.Contracts.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace AniMeido.Tests;

public sealed class WindowsAppNotificationServiceTests
{
    [Fact]
    public async Task Dispose_CancelsRunningHandlerAndDoesNotReleaseItEarly()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance,
            action => { action(); return true; });
        CancellationToken observed = default;
        service.Dispatch(async (_, token) =>
        {
            observed = token;
            entered.SetResult();
            await finish.Task;
        }, Activation());

        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = service.DisposeAsync().AsTask();
            Assert.True(observed.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            service.Dispatch((_, _) => throw new InvalidOperationException("Must not run"), Activation());
        }
        finally
        {
            finish.TrySetResult();
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task RejectedDispatch_UsesTheSameBoundedPendingQueue()
    {
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, _ => false);
        for (var index = 0; index < 40; index++)
        {
            service.Dispatch((_, _) => Task.CompletedTask, Activation());
        }

        var pending = Assert.IsType<Queue<AppNotificationActivation>>(
            typeof(WindowsAppNotificationService)
                .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service));
        Assert.Equal(32, pending.Count);
        await service.DisposeAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task StopActivations_WaitsForRunningHandlerEvenWhenItIgnoresCancellation()
    {
        var entered = NewSignal();
        var finish = NewSignal();
        var logger = new CapturedNotificationLogger();
        await using var service = new WindowsAppNotificationService(
            logger, action => { action(); return true; });
        CancellationToken observed = default;
        service.Dispatch(async (_, token) =>
        {
            observed = token;
            entered.SetResult();
            await finish.Task;
        }, Activation());

        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = service.StopActivationsAsync();

            Assert.True(observed.IsCancellationRequested);
            Assert.False(stopping.IsCompleted);
            Assert.Same(stopping, service.StopActivationsAsync());
        }
        finally
        {
            finish.TrySetResult();
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        Assert.Empty(logger.Events);
    }

    [Fact]
    public async Task StopActivations_WaitsForQueuedDispatchWithoutInvokingTheHandler()
    {
        Action? queued = null;
        var handlerCalled = false;
        var logger = new CapturedNotificationLogger();
        await using var service = new WindowsAppNotificationService(logger, action =>
        {
            queued = action;
            return true;
        });
        service.Dispatch((_, _) =>
        {
            handlerCalled = true;
            return Task.CompletedTask;
        }, Activation());

        var stopping = service.StopActivationsAsync();
        try
        {
            Assert.False(stopping.IsCompleted);
            Assert.False(handlerCalled);
            var invoke = queued;
            Assert.NotNull(invoke);
            queued = null;
            invoke();

            await stopping.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(handlerCalled);
            Assert.Empty(logger.Events);
        }
        finally
        {
            queued?.Invoke();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task StopActivations_NormalHandlerCancellationDoesNotLogAnError()
    {
        var entered = NewSignal();
        var logger = new CapturedNotificationLogger();
        await using var service = new WindowsAppNotificationService(
            logger, action => { action(); return true; });
        service.Dispatch(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, Activation());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopActivationsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(logger.Events);
    }

    [Fact]
    public async Task StopActivations_ClearsPendingAndRejectsNewDispatchAndRegistration()
    {
        var enqueueCount = 0;
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, _ =>
            {
                enqueueCount++;
                return false;
            });
        service.Dispatch((_, _) => Task.CompletedTask, Activation());
        var pending = Assert.IsType<Queue<AppNotificationActivation>>(
            typeof(WindowsAppNotificationService)
                .GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(service));
        Assert.Single(pending);
        Assert.Equal(1, enqueueCount);

        await service.StopActivationsAsync();
        service.Dispatch((_, _) => throw new InvalidOperationException("Must not run"), Activation());

        Assert.Empty(pending);
        Assert.Equal(1, enqueueCount);
        Assert.Throws<ObjectDisposedException>(() => service.RegisterActivationHandler(
            "fixture", (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task StopActivations_LeavesOutboundCancellationAvailableUntilFullDisposal()
    {
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, action => { action(); return true; });
        var stopping = service.StopActivationsAsync();
        await stopping;

        // 不初始化系统通知；验证停接入没有把出站 API 标成已销毁。
        await service.CancelGroupAsync("fixture-group");
        await service.CancelAsync("fixture-group", "fixture-tag");

        await service.DisposeAsync();

        Assert.Same(stopping, service.StopActivationsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CancelGroupAsync("fixture-group"));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CancelAsync("fixture-group", "fixture-tag"));
        Assert.Throws<ObjectDisposedException>(() => service.RegisterActivationHandler(
            "fixture", (_, _) => Task.CompletedTask));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task StopActivations_DoesNotChangeSystemNotificationSupportState()
    {
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, action => { action(); return true; });
        var supported = typeof(WindowsAppNotificationService).GetProperty(nameof(service.IsSupported))!;
        supported.SetValue(service, true);
        try
        {
            await service.StopActivationsAsync();

            Assert.True(service.IsSupported);
        }
        finally
        {
            supported.SetValue(service, false);
        }
    }

    [Fact]
    public async Task StopActivations_PublishesSharedTaskBeforeCancellationCallbackReenters()
    {
        var entered = NewSignal();
        var finish = NewSignal();
        var reentered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, action => { action(); return true; });
        service.Dispatch(async (_, token) =>
        {
            using var registration = token.Register(() =>
                reentered.TrySetResult(service.StopActivationsAsync()));
            entered.SetResult();
            await finish.Task;
        }, Activation());

        Task? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = service.StopActivationsAsync();
            var shared = await reentered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(stopping, shared);
            Assert.False(stopping.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
            if (stopping is not null)
            {
                await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task StopActivations_CancellationCallbackFailureWaitsForHandlerBeforeStopAndDisposeFail()
    {
        var entered = NewSignal();
        var finish = NewSignal();
        var callbackError = new InvalidOperationException("Synthetic cancellation callback failure.");
        var service = new WindowsAppNotificationService(
            NullLogger<WindowsAppNotificationService>.Instance, action => { action(); return true; });
        service.Dispatch(async (_, token) =>
        {
            using var registration = token.Register(() => throw callbackError);
            entered.SetResult();
            await finish.Task;
        }, Activation());

        Task? stopping = null;
        Task? disposing = null;
        Exception? stopFailure = null;
        Exception? disposeFailure = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stopping = service.StopActivationsAsync();
            disposing = service.DisposeAsync().AsTask();

            Assert.False(stopping.IsCompleted);
            Assert.False(disposing.IsCompleted);
            Assert.Same(stopping, service.StopActivationsAsync());
            Assert.Same(disposing, service.DisposeAsync().AsTask());
        }
        finally
        {
            finish.TrySetResult();
            stopping ??= service.StopActivationsAsync();
            disposing ??= service.DisposeAsync().AsTask();
            stopFailure = await Record.ExceptionAsync(() => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
            disposeFailure = await Record.ExceptionAsync(() => disposing.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        var aggregate = Assert.IsType<AggregateException>(stopFailure);
        Assert.Same(callbackError, Assert.Single(aggregate.InnerExceptions));
        Assert.Same(aggregate, disposeFailure);
        Assert.Same(stopping, service.StopActivationsAsync());
        Assert.Same(disposing, service.DisposeAsync().AsTask());
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class CapturedNotificationLogger : ILogger<WindowsAppNotificationService>
    {
        internal List<(LogLevel Level, Exception? Error)> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add((logLevel, exception));
    }

    private static AppNotificationActivation Activation() => new("fixture", "open", new Dictionary<string, string>());

    [Fact]
    public void IsNotificationInfrastructureError_ClassifiesWinRtFailures()
    {
        Assert.True(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new COMException("Element not found", unchecked(
                    (int)0x80070490))));
        Assert.True(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new InvalidOperationException()));
        Assert.False(
            WindowsAppNotificationService.IsNotificationInfrastructureError(
                new OperationCanceledException()));
    }
}
