using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class BackupServiceTests : DbTestBase
{
    [Fact]
    public async Task Backup_RestoreRecoversMultipleTablesAndIntegrity()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var archive = new ArchiveService(DbFactory);
        var tags = new SavedTagService(DbFactory);
        await tracking.SetStatusAsync(17, AnimeTrackingStatus.Watching);
        await archive.UpsertArchiveAsync(17, "备份作品", 8.5, "备份时的笔记");
        await archive.SetAnimeTagsAsync(17, ["个人标签"]);
        await tags.SaveTagAsync("科幻");
        var service = new BackupService(DbFactory, Paths);
        var backup = await service.BackupAsync();

        await tracking.SetStatusAsync(17, AnimeTrackingStatus.Completed);
        await archive.UpsertArchiveAsync(17, "已修改", 1, "不应保留");
        await archive.SetAnimeTagsAsync(17, ["新标签"]);
        await tags.SaveTagAsync("新收藏");
        await tracking.SetStatusAsync(99, AnimeTrackingStatus.Dropped);
        await service.RestoreAsync(backup);

        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(17));
        Assert.Null(await tracking.GetStatusAsync(99));
        var restored = await archive.GetArchiveAsync(17);
        Assert.NotNull(restored);
        Assert.Equal("备份作品", restored.TitleSnapshot);
        Assert.Equal(8.5, restored.PersonalRating);
        Assert.Equal("备份时的笔记", restored.SummaryNote);
        Assert.Equal(new[] { "个人标签" }, await archive.GetAnimeTagsAsync(17));
        Assert.Equal(new[] { "科幻" }, await tags.GetAllSavedTagsAsync());
        await using var connection = await DbFactory.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Backup_CompletedFileCanImmediatelyBeOpenedExclusivelyAndMoved(bool restore)
    {
        await RunProductionMigrationAsync();
        var service = new BackupService(DbFactory, Paths);
        var backup = await service.BackupAsync();
        if (restore) await service.RestoreAsync(backup);
        // 不清理连接池：验证服务自身已经释放备份句柄。
        using (var exclusive = new FileStream(backup, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }
        var moved = backup + ".moved";
        File.Move(backup, moved);
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(moved));
        File.Move(moved, backup);
    }

    [Fact]
    public async Task Backup_EleventhFileDeletesOnlyOldestAcrossAppAndBaseNames()
    {
        await RunProductionMigrationAsync();
        var service = new BackupService(DbFactory, Paths);
        var appBackup = Assert.Single(Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db"));
        var existing = Enumerable.Range(1, 9).Select(day => Path.Combine(
            Paths.BackupDirectory,
            day % 2 == 0 ? $"AniMeido-200001{day:D2}-010203-abcdef01.db" : $"AniMeido-200001{day:D2}.db")).ToArray();
        foreach (var path in existing)
            await File.WriteAllTextAsync(path, Path.GetFileName(path));
        var unrelated = Path.Combine(Paths.BackupDirectory, "other.db");
        await File.WriteAllTextAsync(unrelated, "unrelated");

        var newest = await service.BackupAsync();

        Assert.Equal(10, Directory.GetFiles(Paths.BackupDirectory, "AniMeido-*.db").Length);
        Assert.False(File.Exists(existing[0]));
        foreach (var path in existing.Skip(1))
            Assert.Equal(Path.GetFileName(path), await File.ReadAllTextAsync(path));
        Assert.True(File.Exists(newest));
        Assert.True(File.Exists(appBackup));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
    }

    [Fact]
    public async Task Restore_MissingFileThrowsWithItsPath()
    {
        await RunProductionMigrationAsync();
        var missing = Path.Combine(Paths.BackupDirectory, "missing.db");
        var error = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new BackupService(DbFactory, Paths).RestoreAsync(missing));
        Assert.Equal(missing, error.FileName);
    }
}
