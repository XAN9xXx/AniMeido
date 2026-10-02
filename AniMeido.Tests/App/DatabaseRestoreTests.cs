using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class DatabaseRestoreTests : DbTestBase
{
    [Fact]
    public async Task Restore_SkipsNewestCorruptBackupAndPreservesEveryBackup()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var older = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        var newest = Path.Combine(Paths.BackupDirectory, "AniMeido-20260902.db");
        await CreateBackupAsync(older, 42);
        var backupBytes = await File.ReadAllBytesAsync(older);
        await File.WriteAllBytesAsync(newest, [0, 1, 2, 3]);
        CorruptDatabase();

        Assert.True(await service.TryRestoreFromBackupAsync());

        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeID FROM tracking";
        Assert.Equal(42L, await command.ExecuteScalarAsync());
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(older));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, await File.ReadAllBytesAsync(newest));
    }

    [Fact]
    public async Task Restore_ArchivesOriginalDatabaseAndSidecars()
    {
        var service = new DatabaseService(DbFactory, Paths);
        await CreateBackupAsync(Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db"), 42);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);
        await File.WriteAllBytesAsync(DbPath + "-wal", [1, 2, 3]);
        await File.WriteAllBytesAsync(DbPath + "-shm", [4, 5, 6]);

        Assert.True(await service.TryRestoreFromBackupAsync());

        var archived = Assert.Single(Directory.GetFiles(
            Path.Combine(Paths.BackupDirectory, "corrupt"), "AniMeido-corrupt-*.db"));
        Assert.Equal(original, await File.ReadAllBytesAsync(archived));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(archived + "-wal"));
        Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(archived + "-shm"));
    }

    [Fact]
    public async Task Restore_ExclusiveMainFileLockPreservesOriginalAndBackups()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backups = new[]
        {
            Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db"),
            Path.Combine(Paths.BackupDirectory, "AniMeido-20260902.db"),
        };
        foreach (var backup in backups)
            await CreateBackupAsync(backup, 42);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);

        // Windows 上 FileShare.None 确定阻止主库被复制覆盖或移动。
        using (var locked = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await service.TryRestoreFromBackupAsync());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    [Fact]
    public async Task Restore_LockedSidecarRollsBackAlreadyMovedMainFile()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backup = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        await CreateBackupAsync(backup, 42);
        var backupBytes = await File.ReadAllBytesAsync(backup);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);
        byte[] sidecar = [1, 2, 3];
        await File.WriteAllBytesAsync(DbPath + "-wal", sidecar);

        // 主库移动后，Windows 独占的 WAL 文件让替换中途失败。
        using (var locked = new FileStream(DbPath + "-wal", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(await service.TryRestoreFromBackupAsync());
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.Equal(sidecar, await File.ReadAllBytesAsync(DbPath + "-wal"));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
        Assert.Empty(Directory.GetFiles(Path.Combine(Paths.BackupDirectory, "corrupt")));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    [Fact]
    public async Task Restore_ClearsPooledMainConnectionBeforeReplacement()
    {
        await RunProductionMigrationAsync();
        var service = new DatabaseService(DbFactory, Paths);
        await CreateBackupAsync(Path.Combine(Paths.BackupDirectory, "AniMeido-99991231.db"), 42);
        using (var connection = await DbFactory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO tracking VALUES(99, 1, '2026-09-01T00:00:00Z')";
            await command.ExecuteNonQueryAsync();
        }

        Assert.True(await service.TryRestoreFromBackupAsync());

        using var restored = await DbFactory.OpenAsync();
        using var verify = restored.CreateCommand();
        verify.CommandText = "SELECT AnimeID FROM tracking";
        Assert.Equal(42L, await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT COUNT(*) FROM tracking";
        Assert.Equal(1L, await verify.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Restore_DoesNotTreatCorruptSubdirectoryAsBackups()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var corruptDirectory = Path.Combine(Paths.BackupDirectory, "corrupt");
        Directory.CreateDirectory(corruptDirectory);
        var archived = Path.Combine(corruptDirectory, "AniMeido-corrupt-20260901.db");
        await CreateBackupAsync(archived, 42);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.True(File.Exists(archived));
    }

    [Fact]
    public async Task Restore_AllCorruptBackupsLeaveMainAndBackupsUntouched()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backups = new[]
        {
            Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db"),
            Path.Combine(Paths.BackupDirectory, "AniMeido-20260902.db"),
        };
        foreach (var backup in backups)
            await File.WriteAllBytesAsync(backup, [0, 1, 2, 3]);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        Assert.False(Directory.Exists(Path.Combine(Paths.BackupDirectory, "corrupt")));
    }

    [Fact]
    public async Task Restore_MigrationFailureDoesNotOverwriteMainOrDeleteBackup()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backup = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup,
            Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE anime_plans(AnimeId INTEGER PRIMARY KEY); PRAGMA user_version = 7;";
            await command.ExecuteNonQueryAsync();
        }
        var backupBytes = await File.ReadAllBytesAsync(backup);
        CorruptDatabase();
        var original = await File.ReadAllBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await File.ReadAllBytesAsync(DbPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(backup));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    private static async Task CreateBackupAsync(string path, int animeId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
        await command.ExecuteNonQueryAsync();
        await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
        await DatabaseSchema.RunMigrationsAsync(connection);
        command.CommandText = "INSERT INTO tracking VALUES(@id, 1, '2026-09-01T00:00:00Z')";
        command.Parameters.AddWithValue("@id", animeId);
        await command.ExecuteNonQueryAsync();
    }
}
