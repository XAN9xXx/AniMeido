using AniMeido.Contracts.Models;
using AniMeido.Contracts.Desktop;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PersonalDataAccessTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Exclusive_WaitsForAcceptedWriteAndRejectsNewWrites()
    {
        await RunProductionMigrationAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new TrackingService(DbFactory);
        var writer = OutsideOperation(async () =>
        {
            using var operation = DbFactory.Access.BeginWrite();
            entered.SetResult();
            await release.Task;
            await tracking.SetStatusAsync(1, AnimeTrackingStatus.Watching);
        });
        try
        {
            await entered.Task.WaitAsync(Budget);
            using var import = DbFactory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(default);
            Assert.False(drained.IsCompleted);
            await Assert.ThrowsAsync<PersonalDataBusyException>(() => OutsideOperation(
                () => tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed)));

            release.TrySetResult();
            await writer.WaitAsync(Budget);
            await drained.WaitAsync(Budget);
            Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(1));
            Assert.Null(await tracking.GetStatusAsync(2));
            await tracking.SetStatusAsync(3, AnimeTrackingStatus.Completed);
        }
        finally
        {
            release.TrySetResult();
            await writer.WaitAsync(Budget);
        }
        await tracking.SetStatusAsync(2, AnimeTrackingStatus.Completed);
    }

    [Fact]
    public async Task Exclusive_WaitsForOpenReaders_AndRejectsNewReaders()
    {
        await RunProductionMigrationAsync();
        using var connection = await DbFactory.OpenAsync();
        using var import = DbFactory.Access.BeginExclusive();
        var drained = import.WaitForDrainAsync(default);
        Assert.False(drained.IsCompleted);
        await Assert.ThrowsAsync<PersonalDataBusyException>(() => OutsideOperation(async () =>
        {
            using var other = await DbFactory.OpenAsync();
        }));
        connection.Dispose();
        await drained.WaitAsync(Budget);
        using var owned = await DbFactory.OpenAsync();
        Assert.Equal(System.Data.ConnectionState.Open, owned.State);
    }

    [Fact]
    public async Task CancelledExclusive_ReleasesAdmissionWithoutCancellingAcceptedWork()
    {
        using var connection = await DbFactory.OpenAsync();
        using (var import = DbFactory.Access.BeginExclusive())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => import.WaitForDrainAsync(cancellation.Token));
        }
        using var write = DbFactory.Access.BeginWrite();
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task FailedConnectionOpen_DoesNotPreventLaterExclusiveOperation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DbFactory.OpenAsync(cancellation.Token));
        using var import = DbFactory.Access.BeginExclusive();
        await import.WaitForDrainAsync(default).WaitAsync(Budget);
    }

    [Fact]
    public async Task RecoveryFailure_LeavesDatabaseBlockedAfterImportScopeEnds()
    {
        using (var import = DbFactory.Access.BeginExclusive())
        {
            await import.WaitForDrainAsync(default);
            import.MarkRecoveryFailed();
        }
        Assert.Throws<InvalidOperationException>(() => DbFactory.Access.BeginWrite());
        await Assert.ThrowsAsync<InvalidOperationException>(() => DbFactory.OpenAsync());
        Assert.Throws<InvalidOperationException>(() => DbFactory.Access.BeginExclusive());
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task NestedOperations_ShareAdmissionAcrossAwaitAndReleaseOnce()
    {
        using (var outer = DbFactory.Access.BeginWrite())
        {
            await Task.Yield();
            using var inner = DbFactory.Access.BeginWrite();
            inner.Dispose();
        }
        using var import = DbFactory.Access.BeginExclusive();
        await import.WaitForDrainAsync(default).WaitAsync(Budget);
        using (var nested = DbFactory.Access.BeginExclusive())
        {
            await nested.WaitForDrainAsync(default).WaitAsync(Budget);
            using var write = DbFactory.Access.BeginWrite();
            using var connection = await DbFactory.OpenAsync();
        }
        await Assert.ThrowsAsync<PersonalDataBusyException>(() => OutsideOperation(() =>
        {
            using var denied = DbFactory.Access.BeginWrite();
            return Task.CompletedTask;
        }));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task NestedWrite_OuterDisposalDoesNotReleaseAcceptedInnerOperation()
    {
        await RunProductionMigrationAsync();
        var outerReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracking = new TrackingService(DbFactory);
        var writer = OutsideOperation(async () =>
        {
            using var outer = DbFactory.Access.BeginWrite();
            using var inner = DbFactory.Access.BeginWrite();
            outer.Dispose();
            outerReleased.TrySetResult();
            await releaseInner.Task;
            await tracking.SetStatusAsync(43, AnimeTrackingStatus.Watching);
        });

        try
        {
            await outerReleased.Task.WaitAsync(Budget);
            using var import = DbFactory.Access.BeginExclusive();
            var drained = import.WaitForDrainAsync(default);
            Assert.False(drained.IsCompleted);

            releaseInner.TrySetResult();
            await writer.WaitAsync(Budget);
            await drained.WaitAsync(Budget);
            Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(43));
        }
        finally
        {
            releaseInner.TrySetResult();
            await writer.WaitAsync(Budget);
        }
    }

    internal static Task OutsideOperation(Func<Task> action)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(action);
    }

    [Fact]
    public async Task Exclusive_RejectsScreenshotDeletionBeforeFileOrRecordChanges()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(Paths.RootDirectory, "screenshot.png");
        await File.WriteAllTextAsync(path, "fixture");
        var archive = new ArchiveService(DbFactory);
        var screenshot = new AnimeScreenshot("fixture", path, "fixture-not-a-hash", DateTimeOffset.UtcNow,
            "window", "process", 1, 1, null, null, null, null, "", true);
        await archive.InsertScreenshotAsync(screenshot);
        var capabilities = new UnusedCaptureCapabilities();
        var service = new ScreenshotArchiveService(archive, capabilities, capabilities, capabilities);
        using var import = DbFactory.Access.BeginExclusive();
        await import.WaitForDrainAsync(default);

        await Assert.ThrowsAsync<PersonalDataBusyException>(() => OutsideOperation(() => service.DeleteAsync(screenshot)));
        await Assert.ThrowsAsync<PersonalDataBusyException>(() => OutsideOperation(() => service.CaptureAsync()));

        Assert.Equal("fixture", await File.ReadAllTextAsync(path));
        Assert.Equal("fixture", Assert.Single(await archive.GetScreenshotsAsync()).ScreenshotId);
    }

    [Fact]
    public async Task ScreenshotDeletion_RecycleFailureKeepsRecord()
    {
        await RunProductionMigrationAsync();
        var path = Path.Combine(Paths.RootDirectory, "retained.png");
        await File.WriteAllTextAsync(path, "fixture");
        var archive = new ArchiveService(DbFactory);
        var screenshot = new AnimeScreenshot("retained", path, "", DateTimeOffset.UtcNow,
            "", "", 1, 1, null, null, null, null, "", true);
        await archive.InsertScreenshotAsync(screenshot);
        var capabilities = new UnusedCaptureCapabilities();
        var recycleBin = new RecordingRecycleBin(fail: true);
        var service = new ScreenshotArchiveService(archive, capabilities, capabilities, recycleBin);

        await Assert.ThrowsAsync<IOException>(() => service.DeleteAsync(screenshot));

        Assert.Equal(path, recycleBin.Path);
        Assert.True(File.Exists(path));
        Assert.Equal("retained", Assert.Single(await archive.GetScreenshotsAsync()).ScreenshotId);
    }

    private sealed class RecordingRecycleBin(bool fail) : IScreenshotRecycleBin
    {
        public string? Path { get; private set; }

        public void MoveToRecycleBin(string path)
        {
            Path = path;
            if (fail)
                throw new IOException("fixture recycle failure");
        }
    }

    private sealed class UnusedCaptureCapabilities : IForegroundWindowCaptureService, IActiveAnimePlaybackContextProvider, IScreenshotRecycleBin
    {
        public void MoveToRecycleBin(string path)
            => throw new InvalidOperationException("准入拒绝前不得移动截图。");

        public bool IsSupported => true;
        public Task<ForegroundWindowCapture> CaptureAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("准入拒绝前不得调用截图能力。");
        public Task<ActiveAnimePlaybackContext?> GetActiveContextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("准入拒绝前不得读取播放状态。");
    }
}
