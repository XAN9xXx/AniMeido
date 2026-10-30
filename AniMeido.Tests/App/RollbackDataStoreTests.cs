using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class RollbackDataStoreTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task ShutdownWaitsForAcceptedBackgroundDeletionAndRejectsNewWork()
    {
        using var paths = new MockAppDataPaths();
        var target = Path.Combine(paths.BackupDirectory, "update", "AniMeido-before-restore-20261026-120000");
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "saved.db"), "isolated test data");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new RollbackDataStore(paths, () =>
        {
            Assert.Null(SynchronizationContext.Current);
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the deletion checkpoint.");
        });
        var deleting = store.DeleteAsync(target);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = store.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            Assert.Same(stopping, store.DisposeAsync().AsTask());
            await Assert.ThrowsAsync<ObjectDisposedException>(() => store.ListAsync());

            release.Set();
            await deleting.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(Directory.Exists(target));
        }
        finally
        {
            release.Set();
            await Task.WhenAny(deleting).WaitAsync(TimeSpan.FromSeconds(5));
            await deleting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task ListParsesPrereleaseVersionAndTimeAndIgnoresOtherData()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        Directory.CreateDirectory(Path.Combine(root, "AniMeido-rollback-from-2.4.0-preview.1-20261004-123456"));
        Directory.CreateDirectory(Path.Combine(root, "before-update"));
        await using var store = new RollbackDataStore(paths);
        var entry = Assert.Single(await store.ListAsync());
        Assert.Equal("2.4.0-preview.1", entry.Version);
        Assert.Equal(new DateTime(2026, 10, 4, 12, 34, 56), entry.Time);
    }

    [Theory]
    [InlineData("elsewhere/AniMeido-rollback-from-2.3.0-20261004-123456")]
    [InlineData("Backups/update/before-update")]
    [InlineData("Backups/update/nested/AniMeido-rollback-from-2.3.0-20261004-123456")]
    public async Task DeleteRejectsNonDirectOrNonRollbackDirectory(string relative)
    {
        using var paths = new MockAppDataPaths();
        var target = Path.Combine(paths.RootDirectory, relative);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        await using var store = new RollbackDataStore(paths);
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(target));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public async Task DeleteOnlySelectedArchiveAndNeverCurrentDatabaseOrBeforeUpdateBackup()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        var target = Path.Combine(root, "AniMeido-rollback-from-2.3.0-20261004-123456");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "AniMeido.db"), "archive");
        File.WriteAllText(paths.DatabasePath, "current");
        var before = Path.Combine(root, "AniMeido-before-v7-to-v8.db");
        File.WriteAllText(before, "before");
        await using var store = new RollbackDataStore(paths);
        await store.DeleteAsync(target);
        Assert.False(Directory.Exists(target));
        Assert.Equal("current", File.ReadAllText(paths.DatabasePath));
        Assert.Equal("before", File.ReadAllText(before));
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task MissingRootHasNoEntries()
    {
        using var paths = new MockAppDataPaths();
        await using var store = new RollbackDataStore(paths);
        Assert.Empty(await store.ListAsync());
    }

    [Fact]
    public async Task ListIncludesBeforeRestoreDataWithSourceLabelAndAllowsDeletingOnlySelectedDirectory()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        var old = Path.Combine(root, "AniMeido-rollback-from-2.4.0-20261004-123456");
        var restored = Path.Combine(root, "AniMeido-before-restore-20261019-123456-unique");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(restored);
        File.WriteAllText(Path.Combine(restored, "AniMeido.db"), "synthetic preserved data");
        File.WriteAllText(paths.DatabasePath, "current");
        await using var store = new RollbackDataStore(paths);
        var entries = await store.ListAsync();
        Assert.Equal(2, entries.Count);
        Assert.Equal(restored, entries[0].Path);
        Assert.True(entries[0].IsBeforeRestore);
        Assert.Contains("恢复前数据", entries[0].DisplayText);
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56), entries[0].Time);
        Assert.False(entries[1].IsBeforeRestore);
        await store.DeleteAsync(restored);
        Assert.False(Directory.Exists(restored));
        Assert.Equal(old, Assert.Single(await store.ListAsync()).Path);
        Assert.Equal("current", File.ReadAllText(paths.DatabasePath));
    }

    [Fact]
    public async Task ListAndDeleteRejectReparseDirectoryAndDeletionRejectsLinkedChildren()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(paths.RootDirectory, "outside");
        Directory.CreateDirectory(outside);
        var keep = Path.Combine(outside, "keep");
        File.WriteAllText(keep, "keep");
        var link = Path.Combine(root, "AniMeido-before-restore-link");
        Directory.CreateSymbolicLink(link, outside);
        await using var store = new RollbackDataStore(paths);
        await Assert.ThrowsAsync<IOException>(() => store.ListAsync());
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(link));
        Directory.Delete(link);
        var target = Path.Combine(root, "AniMeido-before-restore-20261019-123456");
        Directory.CreateDirectory(target);
        var child = Path.Combine(target, "linked-file");
        File.CreateSymbolicLink(child, keep);
        await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(target));
        Assert.Equal("keep", File.ReadAllText(keep));
        File.Delete(child);
    }

    [Fact]
    public async Task EvenEmptyLinkedUpdateRootIsRejected()
    {
        using var paths = new MockAppDataPaths();
        var outside = Path.Combine(paths.RootDirectory, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(paths.BackupDirectory);
        var root = Path.Combine(paths.BackupDirectory, "update");
        Directory.CreateSymbolicLink(root, outside);
        await using var store = new RollbackDataStore(paths);
        await Assert.ThrowsAsync<IOException>(() => store.ListAsync());
        Directory.Delete(root);
    }
}
