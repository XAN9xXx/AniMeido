using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class RollbackDataStoreTests
{
    [Fact]
    public void ListParsesPrereleaseVersionAndTimeAndIgnoresOtherData()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        Directory.CreateDirectory(Path.Combine(root, "AniMeido-rollback-from-2.4.0-preview.1-20261004-123456"));
        Directory.CreateDirectory(Path.Combine(root, "before-update"));
        var entry = Assert.Single(new RollbackDataStore(paths).List());
        Assert.Equal("2.4.0-preview.1", entry.Version);
        Assert.Equal(new DateTime(2026, 10, 4, 12, 34, 56), entry.Time);
    }

    [Theory]
    [InlineData("elsewhere/AniMeido-rollback-from-2.3.0-20261004-123456")]
    [InlineData("Backups/update/before-update")]
    [InlineData("Backups/update/nested/AniMeido-rollback-from-2.3.0-20261004-123456")]
    public void DeleteRejectsNonDirectOrNonRollbackDirectory(string relative)
    {
        using var paths = new MockAppDataPaths();
        var target = Path.Combine(paths.RootDirectory, relative);
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");
        Assert.Throws<IOException>(() => new RollbackDataStore(paths).Delete(target));
        Assert.True(Directory.Exists(target));
    }

    [Fact]
    public void DeleteOnlySelectedArchiveAndNeverCurrentDatabaseOrBeforeUpdateBackup()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        var target = Path.Combine(root, "AniMeido-rollback-from-2.3.0-20261004-123456");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "AniMeido.db"), "archive");
        File.WriteAllText(paths.DatabasePath, "current");
        var before = Path.Combine(root, "AniMeido-before-v7-to-v8.db");
        File.WriteAllText(before, "before");
        var store = new RollbackDataStore(paths);
        store.Delete(target);
        Assert.False(Directory.Exists(target));
        Assert.Equal("current", File.ReadAllText(paths.DatabasePath));
        Assert.Equal("before", File.ReadAllText(before));
        Assert.Empty(store.List());
    }

    [Fact]
    public void MissingRootHasNoEntries()
    {
        using var paths = new MockAppDataPaths();
        Assert.Empty(new RollbackDataStore(paths).List());
    }

    [Fact]
    public void ListIncludesBeforeRestoreDataWithSourceLabelAndAllowsDeletingOnlySelectedDirectory()
    {
        using var paths = new MockAppDataPaths();
        var root = Path.Combine(paths.BackupDirectory, "update");
        var old = Path.Combine(root, "AniMeido-rollback-from-2.4.0-20261004-123456");
        var restored = Path.Combine(root, "AniMeido-before-restore-20261019-123456-unique");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(restored);
        File.WriteAllText(Path.Combine(restored, "AniMeido.db"), "synthetic preserved data");
        File.WriteAllText(paths.DatabasePath, "current");
        var store = new RollbackDataStore(paths);
        var entries = store.List();
        Assert.Equal(2, entries.Count);
        Assert.Equal(restored, entries[0].Path);
        Assert.True(entries[0].IsBeforeRestore);
        Assert.Contains("恢复前数据", entries[0].DisplayText);
        Assert.Equal(new DateTime(2026, 10, 19, 12, 34, 56), entries[0].Time);
        Assert.False(entries[1].IsBeforeRestore);
        store.Delete(restored);
        Assert.False(Directory.Exists(restored));
        Assert.Equal(old, Assert.Single(store.List()).Path);
        Assert.Equal("current", File.ReadAllText(paths.DatabasePath));
    }

    [Fact]
    public void ListAndDeleteRejectReparseDirectoryAndDeletionRejectsLinkedChildren()
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
        var store = new RollbackDataStore(paths);
        Assert.Throws<IOException>(() => store.List());
        Assert.Throws<IOException>(() => store.Delete(link));
        Directory.Delete(link);
        var target = Path.Combine(root, "AniMeido-before-restore-20261019-123456");
        Directory.CreateDirectory(target);
        var child = Path.Combine(target, "linked-file");
        File.CreateSymbolicLink(child, keep);
        Assert.Throws<IOException>(() => store.Delete(target));
        Assert.Equal("keep", File.ReadAllText(keep));
        File.Delete(child);
    }

    [Fact]
    public void EvenEmptyLinkedUpdateRootIsRejected()
    {
        using var paths = new MockAppDataPaths();
        var outside = Path.Combine(paths.RootDirectory, "outside");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(paths.BackupDirectory);
        var root = Path.Combine(paths.BackupDirectory, "update");
        Directory.CreateSymbolicLink(root, outside);
        Assert.Throws<IOException>(() => new RollbackDataStore(paths).List());
        Directory.Delete(root);
    }
}
