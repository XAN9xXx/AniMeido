using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class StartupDialogCoordinatorTests : IAsyncLifetime
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);
    private readonly MockAppDataPaths _paths = new();
    private readonly DesktopSettingsStore _settings;
    private readonly StartupDialogCoordinator _coordinator;

    public StartupDialogCoordinatorTests()
    {
        _settings = new DesktopSettingsStore(_paths);
        _coordinator = new StartupDialogCoordinator(new PrivacyService(_settings), _settings);
    }

    public async Task InitializeAsync()
    {
        // 已确认的设置走真实跳过分支，不创建 ContentDialog，也不验证原生弹窗呈现。
        await _settings.UpdateAsync(value => value with
        {
            PrivacyAccepted = true,
            AnnouncementShown = true,
        });
    }

    public Task DisposeAsync()
    {
        _paths.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WaitBeforeStartupDialogs_DoesNotMistakeNotStartedForCompleted()
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = _coordinator.WaitForCompletionAsync(cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveWaiterAsync(waiting);
        }
    }

    [Fact]
    public async Task AlreadyAcceptedPrivacyAndAnnouncement_CompleteTheStartupOwnerWithoutShowingDialogs()
    {
        using var cancellation = new CancellationTokenSource();
        var waiting = _coordinator.WaitForCompletionAsync(cancellation.Token);
        try
        {
            Assert.False(waiting.IsCompleted);

            await _coordinator.ShowStartupDialogsAsync(null!, CancellationToken.None).WaitAsync(Budget);
            await waiting.WaitAsync(Budget);

            var settings = await _settings.LoadAsync();
            Assert.True(settings.PrivacyAccepted);
            Assert.True(settings.AnnouncementShown);
            await _coordinator.WaitForCompletionAsync(CancellationToken.None).WaitAsync(Budget);
        }
        finally
        {
            cancellation.Cancel();
            await ObserveWaiterAsync(waiting);
        }
    }

    [Fact]
    public async Task PreCancelledStartup_DoesNotCompleteTheOwnerAndAllowsALaterSuccessfulAttempt()
    {
        using var cancelledAttempt = new CancellationTokenSource();
        cancelledAttempt.Cancel();
        using var waitingCancellation = new CancellationTokenSource();
        var waiting = _coordinator.WaitForCompletionAsync(waitingCancellation.Token);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _coordinator.ShowStartupDialogsAsync(null!, cancelledAttempt.Token).WaitAsync(Budget));

            Assert.False(waiting.IsCompleted);

            await _coordinator.ShowStartupDialogsAsync(null!, CancellationToken.None).WaitAsync(Budget);
            await waiting.WaitAsync(Budget);
        }
        finally
        {
            waitingCancellation.Cancel();
            await ObserveWaiterAsync(waiting);
        }
    }

    [Fact]
    public async Task CancellingOneWaiter_DoesNotCancelAnotherWaiterOrPoisonSharedCompletion()
    {
        using var cancelledWaiter = new CancellationTokenSource();
        using var remainingWaiter = new CancellationTokenSource();
        var cancelled = _coordinator.WaitForCompletionAsync(cancelledWaiter.Token);
        var remaining = _coordinator.WaitForCompletionAsync(remainingWaiter.Token);
        try
        {
            cancelledWaiter.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Budget));
            Assert.False(remaining.IsCompleted);

            await _coordinator.ShowStartupDialogsAsync(null!, CancellationToken.None).WaitAsync(Budget);
            await remaining.WaitAsync(Budget);
            await _coordinator.WaitForCompletionAsync(CancellationToken.None).WaitAsync(Budget);
        }
        finally
        {
            cancelledWaiter.Cancel();
            remainingWaiter.Cancel();
            await ObserveWaiterAsync(cancelled);
            await ObserveWaiterAsync(remaining);
        }
    }

    private static async Task ObserveWaiterAsync(Task waiting)
    {
        try
        {
            await waiting.WaitAsync(Budget);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
