using AniMeido.Updates.Install;
using Xunit;
using static AniMeido.Updates.Tests.RestoreDatabaseFixture;

namespace AniMeido.Updates.Tests;

public sealed class DatabaseRestoreCleanupTests
{
    [WindowsFileSharingTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CleanupFailureIsReportedWithoutChangingTheRestoreOutcome(bool failRestore)
    {
        using var temp = new InstallTestRoot();
        var database = MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        var original = File.ReadAllBytes(database);
        var replacement = File.ReadAllBytes(Path.Combine(source, "AniMeido.db"));
        var archive = DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data"));
        var retained = failRestore ? database + ".restore-tmp" : Path.Combine(archive, "AniMeido.db.detached");
        FileStream? held = null;
        try
        {
            var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source, archive, 8, _ =>
            {
                // 只锁定本次恢复已经创建的临时文件，不阻止归档或原数据的补偿。
                held = new FileStream(retained, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (failRestore)
                {
                    throw new IOException("fixture restore failure");
                }
            });

            Assert.Equal(!failRestore, result.Success);
            Assert.Equal(failRestore ? original : replacement, File.ReadAllBytes(database));
            if (failRestore)
            {
                Assert.Contains("fixture restore failure", result.Error);
            }
            else
            {
                Assert.Null(result.Error);
            }

            var cleanup = Assert.Single(result.CleanupErrors);
            Assert.Equal(Path.GetFullPath(retained), cleanup.Path);
            Assert.False(string.IsNullOrWhiteSpace(cleanup.Message));
            Assert.True(File.Exists(retained));
            temp.Manager.ReportRestoreCleanupErrors(result);
            Assert.Contains(Path.GetFullPath(retained), Assert.Single(temp.Logs));
        }
        finally
        {
            held?.Dispose();
        }
    }

    [Fact]
    public void SuccessfulCleanupDoesNotCreateAWarning()
    {
        using var temp = new InstallTestRoot();
        MakeDatabase(temp, "data/AniMeido.db", 7);
        var source = MakeSource(temp, 8);
        var archive = DatabaseRollback.GetRestoreArchiveDirectory(temp.PathFor("data"));

        var result = DatabaseRollback.RestoreArchived(temp.PathFor("data"), source, archive, 8);

        Assert.True(result.Success);
        Assert.Empty(result.CleanupErrors);
        temp.Manager.ReportRestoreCleanupErrors(result);
        Assert.Empty(temp.Logs);
    }
}
