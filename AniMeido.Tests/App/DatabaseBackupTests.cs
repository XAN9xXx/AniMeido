using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class DatabaseBackupTests : DbTestBase
{
    [Fact]
    public async Task Backup_CompletedFileCanImmediatelyBeOpenedExclusivelyAndMoved()
    {
        // 不调用 InitializeAsync，避免它在后台再发起一次备份与本测试竞争。
        await CreateBaseTablesAsync();
        var service = new DatabaseService(DbFactory, Paths);

        await service.BackupAsync(throwOnFailure: true);

        var backup = Assert.Single(Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db"));
        // 不清理连接池：验证服务自身已经释放备份句柄。
        using (var exclusive = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }
        var moved = backup + ".moved";
        File.Move(backup, moved);
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(moved));
    }
}
