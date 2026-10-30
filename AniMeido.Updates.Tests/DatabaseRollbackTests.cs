using System.Text;
using AniMeido.Updates.Install;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class DatabaseRollbackTests
{
    [Fact]
    public void RestoreArchivesDatabaseAndSidecarsThenReplacesDatabase()
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.MakeDatabase("before.db", "old");
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");
        var current = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0");
        var result = DatabaseRollback.Restore(temp.PathFor("data"), backup, archive);
        Assert.True(result.Success, result.Error);
        Assert.Equal(archive, result.ArchiveDirectory);
        Assert.Equal(current, File.ReadAllBytes(Path.Combine(archive, "AniMeido.db")));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(archive, "AniMeido.db-wal")));
        Assert.Equal("shm", File.ReadAllText(Path.Combine(archive, "AniMeido.db-shm")));
        Assert.Equal(File.ReadAllBytes(backup), File.ReadAllBytes(database));
        Assert.False(File.Exists(database + "-wal"));
        Assert.False(File.Exists(database + "-shm"));
        Assert.False(File.Exists(database + ".restore-tmp"));
        Assert.DoesNotContain("AniMeido-before-", Path.GetFileName(archive));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(archive)!, "*.tmp-*"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not SQLite")]
    [InlineData("SQLite format 3Xwrong")]
    public void MissingOrBadHeaderChangesNoFilesIncludingExistingTemporaryFile(string? payload)
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.PathFor("before.db");
        if (payload is not null)
        {
            File.WriteAllBytes(backup, Encoding.ASCII.GetBytes(payload));
        }
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");
        File.WriteAllText(database + ".restore-tmp", "prior temporary file");
        var before = Snapshot(temp.Root);
        var result = DatabaseRollback.Restore(temp.PathFor("data"), backup,
            DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0"));
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
        Assert.Equal(before, Snapshot(temp.Root));
        Assert.False(Directory.Exists(temp.PathFor("data/Backups")));
    }

    [WindowsFileSharingFact]
    public void LockedCurrentSidecarMakesArchiveFailWithoutReplacingDatabase()
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.MakeDatabase("before.db", "old");
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");
        var original = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0");
        using (var held = new FileStream(database + "-wal", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = DatabaseRollback.Restore(temp.PathFor("data"), backup, archive);
            Assert.False(result.Success);
            Assert.Null(result.ArchiveDirectory);
            Assert.Equal(original, File.ReadAllBytes(database));
            Assert.True(File.Exists(database + "-shm"));
            Assert.False(Directory.Exists(archive));
        }
        Assert.Equal("wal", File.ReadAllText(database + "-wal"));
    }

    [Fact]
    public void FailedReplacementKeepsCompletedArchiveAndReportsItsLocation()
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.MakeDatabase("before.db", "old");
        Directory.CreateDirectory(database + ".restore-tmp");
        var original = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0");
        var result = DatabaseRollback.Restore(temp.PathFor("data"), backup, archive);
        Assert.False(result.Success);
        Assert.Equal(archive, result.ArchiveDirectory);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(archive, "AniMeido.db")));
    }

    [WindowsFileSharingFact]
    public void SidecarDeletionFailureKeepsCompletedArchiveAndDoesNotReplaceDatabase()
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.MakeDatabase("before.db", "old");
        File.WriteAllText(database + "-wal", "wal");
        File.WriteAllText(database + "-shm", "shm");
        var original = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0");
        // 可读取所以另存能完成，但不共享删除权限，必须中止替换。
        using var held = new FileStream(database + "-wal", FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = DatabaseRollback.Restore(temp.PathFor("data"), backup, archive);
        Assert.False(result.Success);
        Assert.Equal(archive, result.ArchiveDirectory);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal("wal", File.ReadAllText(Path.Combine(archive, "AniMeido.db-wal")));
        Assert.True(File.Exists(database + "-shm"));
        Assert.False(File.Exists(database + ".restore-tmp"));
    }

    [Fact]
    public void RejectsArchiveOutsideOwnedUpdateDirectory()
    {
        using var temp = new InstallTestRoot();
        var backup = temp.MakeDatabase("before.db", "old");
        temp.MakeDatabase("data/AniMeido.db", "new");
        Assert.Throws<ArgumentException>(() => DatabaseRollback.Restore(temp.PathFor("data"), backup, temp.PathFor("outside")));
        Assert.Throws<ArgumentException>(() => DatabaseRollback.Restore(temp.PathFor("data"), backup,
            temp.PathFor("data/Backups/update/AniMeido-before-unsafe")));
    }

    [WindowsFileSharingTheory]
    [InlineData("-shm")]
    [InlineData("")]
    public void FailureAfterDetachingWalRestoresAllOriginalFiles(string lockedSuffix)
    {
        using var temp = new InstallTestRoot();
        var database = temp.MakeDatabase("data/AniMeido.db", "new");
        var backup = temp.MakeDatabase("before.db", "old");
        File.WriteAllText(database + "-wal", "committed but not checkpointed");
        File.WriteAllText(database + "-shm", "shm");
        var original = File.ReadAllBytes(database);
        var archive = DatabaseRollback.GetArchiveDirectory(temp.PathFor("data"), "2.2.0");
        // 允许归档读取，但阻止移动 SHM 或替换主库；此时 WAL 已经移走。
        using var held = new FileStream(database + lockedSuffix, FileMode.Open, FileAccess.Read, FileShare.Read);

        var result = DatabaseRollback.Restore(temp.PathFor("data"), backup, archive);

        Assert.False(result.Success);
        Assert.Equal(archive, result.ArchiveDirectory);
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Equal("committed but not checkpointed", File.ReadAllText(database + "-wal"));
        Assert.Equal("shm", File.ReadAllText(database + "-shm"));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(archive, "AniMeido.db")));
        Assert.Equal("committed but not checkpointed", File.ReadAllText(Path.Combine(archive, "AniMeido.db-wal")));
        Assert.Empty(Directory.GetFiles(archive, "*.detached"));
        Assert.False(File.Exists(database + ".restore-tmp"));
    }

    private static string[] Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => path + ":" + Convert.ToBase64String(File.ReadAllBytes(path))).ToArray();
}
