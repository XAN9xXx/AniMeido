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
}
