using AniMeido.App.Services;

using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class DatabaseBackupTests : DbTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_RollingCleanupDoesNotTouchUpdateOrCorruptDirectories(bool useBaseService)
    {
        // 不调用 InitializeAsync，顶层数量只由本测试控制。
        await CreateBaseTablesAsync();
        var service = new DatabaseService(DbFactory, Paths);
        var rolling = Enumerable.Range(1, 12).Select(day => Path.Combine(
            Paths.BackupDirectory, $"AniMeido-200001{day:D2}-010203-abcdef01.db")).ToArray();
        foreach (var path in rolling)
            await File.WriteAllTextAsync(path, Path.GetFileName(path));
        var updateDirectory = Path.Combine(Paths.BackupDirectory, "update");
        var corruptDirectory = Path.Combine(Paths.BackupDirectory, "corrupt");
        Directory.CreateDirectory(updateDirectory);
        Directory.CreateDirectory(corruptDirectory);
        var protectedFiles = new[]
        {
            Path.Combine(updateDirectory, "AniMeido-before-v4-to-v8-19990101-010203-abcdef01.db"),
            Path.Combine(updateDirectory, "AniMeido-before-v5-to-v8-19990102-010203-abcdef02.db"),
            Path.Combine(updateDirectory, "AniMeido-rollback-19990103.db"),
            Path.Combine(corruptDirectory, "AniMeido-corrupt-19990101.db"),
            Path.Combine(Paths.BackupDirectory, "other.db"),
        };
        foreach (var path in protectedFiles)
            await File.WriteAllTextAsync(path, Path.GetFileName(path));

        if (useBaseService)
            await new BackupService(DbFactory, Paths).BackupAsync();
        else
            await service.BackupAsync(throwOnFailure: true);

        Assert.Equal(10, Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db").Length);
        foreach (var path in rolling.Take(3))
            Assert.False(File.Exists(path));
        foreach (var path in rolling.TakeLast(9).Concat(protectedFiles))
            Assert.Equal(Path.GetFileName(path), await File.ReadAllTextAsync(path));
    }

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
