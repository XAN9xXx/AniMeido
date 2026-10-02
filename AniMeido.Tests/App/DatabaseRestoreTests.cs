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
        var backupBytes = await ReadTestFileBytesAsync(older);
        await File.WriteAllBytesAsync(newest, [0, 1, 2, 3]);
        CorruptDatabase();

        Assert.True(await service.TryRestoreFromBackupAsync());

        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeID FROM tracking";
        Assert.Equal(42L, await command.ExecuteScalarAsync());
        Assert.Equal(backupBytes, await ReadTestFileBytesAsync(older));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, await ReadTestFileBytesAsync(newest));
    }

    [Fact]
    public async Task Restore_ArchivesOriginalDatabaseAndSidecars()
    {
        var service = new DatabaseService(DbFactory, Paths);
        await CreateBackupAsync(Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db"), 42);
        CorruptDatabase();
        var original = await ReadTestFileBytesAsync(DbPath);
        await File.WriteAllBytesAsync(DbPath + "-wal", [1, 2, 3]);
        await File.WriteAllBytesAsync(DbPath + "-shm", [4, 5, 6]);

        Assert.True(await service.TryRestoreFromBackupAsync());

        var archived = Assert.Single(Directory.GetFiles(
            Path.Combine(Paths.BackupDirectory, "corrupt"), "AniMeido-corrupt-*.db"));
        Assert.Equal(original, await ReadTestFileBytesAsync(archived));
        Assert.Equal(new byte[] { 1, 2, 3 }, await ReadTestFileBytesAsync(archived + "-wal"));
        Assert.Equal(new byte[] { 4, 5, 6 }, await ReadTestFileBytesAsync(archived + "-shm"));
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
        var original = await ReadTestFileBytesAsync(DbPath);

        // Windows 上 FileShare.None 确定阻止主库被复制覆盖或移动。
        using (var locked = new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<DatabaseRestoreUnavailableException>(service.TryRestoreFromBackupAsync);
        }

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
        Assert.All(backups, backup => Assert.True(File.Exists(backup)));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    [Fact]
    public async Task Restore_LockedSidecarRollsBackAlreadyMovedMainFile()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backup = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        await CreateBackupAsync(backup, 42);
        var backupBytes = await ReadTestFileBytesAsync(backup);
        CorruptDatabase();
        var original = await ReadTestFileBytesAsync(DbPath);
        byte[] sidecar = [1, 2, 3];
        await File.WriteAllBytesAsync(DbPath + "-wal", sidecar);

        // 主库移动后，Windows 独占的 WAL 文件让替换中途失败。
        using (var locked = new FileStream(DbPath + "-wal", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<DatabaseRestoreUnavailableException>(service.TryRestoreFromBackupAsync);
        }

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
        Assert.Equal(sidecar, await ReadTestFileBytesAsync(DbPath + "-wal"));
        Assert.Equal(backupBytes, await ReadTestFileBytesAsync(backup));
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
        var original = await ReadTestFileBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
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
        var original = await ReadTestFileBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
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
        var backupBytes = await ReadTestFileBytesAsync(backup);
        CorruptDatabase();
        var original = await ReadTestFileBytesAsync(DbPath);

        Assert.False(await service.TryRestoreFromBackupAsync());

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
        Assert.Equal(backupBytes, await ReadTestFileBytesAsync(backup));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    [Fact]
    public async Task Restore_LockedNewestBackupStopsWithoutUsingOlderBackup()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var older = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        var newest = Path.Combine(Paths.BackupDirectory, "AniMeido-20260902.db");
        await CreateBackupAsync(older, 42);
        await CreateBackupAsync(newest, 84);
        var olderBytes = await ReadTestFileBytesAsync(older);
        var newestBytes = await ReadTestFileBytesAsync(newest);
        CorruptDatabase();
        var original = await ReadTestFileBytesAsync(DbPath);

        using (var locked = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<DatabaseRestoreUnavailableException>(service.TryRestoreFromBackupAsync);
            var cause = Assert.IsType<SqliteException>(exception.InnerException);
            Assert.Contains(cause.SqliteErrorCode, new[] { 5, 6, 10, 14 });
        }

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newest));
        Assert.Equal(olderBytes, await ReadTestFileBytesAsync(older));
        Assert.Equal(newestBytes, await ReadTestFileBytesAsync(newest));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
        Assert.False(Directory.Exists(Path.Combine(Paths.BackupDirectory, "corrupt")));
    }

    [Fact]
    public async Task Restore_ReleasedNewestBackupLockRestoresNewestData()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var older = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        var newest = Path.Combine(Paths.BackupDirectory, "AniMeido-20260902.db");
        await CreateBackupAsync(older, 42);
        await CreateBackupAsync(newest, 84);
        CorruptDatabase();

        using var locked = new FileStream(newest, FileMode.Open, FileAccess.Read, FileShare.None);
        var releaseLock = Task.Run(async () =>
        {
            await Task.Delay(300);
            locked.Dispose();
        });
        try
        {
            Assert.True(await service.TryRestoreFromBackupAsync());
        }
        finally
        {
            await releaseLock;
        }

        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT AnimeID FROM tracking";
        Assert.Equal(84L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT COUNT(*) FROM tracking";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
        Assert.True(File.Exists(older));
        Assert.True(File.Exists(newest));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
    }

    [Fact]
    public async Task Initialize_LockedOnlyBackupReportsTemporaryUnavailability()
    {
        var service = new DatabaseService(DbFactory, Paths);
        var backup = Path.Combine(Paths.BackupDirectory, "AniMeido-20260901.db");
        await CreateBackupAsync(backup, 42);
        CorruptDatabase();
        var original = await ReadTestFileBytesAsync(DbPath);

        using (var locked = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(service.InitializeAsync);
            Assert.Equal("数据库文件已损坏；备份文件暂时被其他程序占用，未执行恢复。请稍后重启 AniMeido 再试。", exception.Message);
            var unavailable = Assert.IsType<DatabaseRestoreUnavailableException>(exception.InnerException);
            Assert.IsType<SqliteException>(unavailable.InnerException);
        }

        Assert.Equal(original, await ReadTestFileBytesAsync(DbPath));
        Assert.True(File.Exists(backup));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(DbPath)!, ".AniMeido-restore-*.db*"));
        Assert.False(Directory.Exists(Path.Combine(Paths.BackupDirectory, "corrupt")));
    }

    // 文件可能刚被 SQLite 写完，或正被外部程序扫描；只重试短暂的共享冲突。
    private static async Task<byte[]> ReadTestFileBytesAsync(string path)
    {
        const int maxAttempts = 10;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var file = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, options: FileOptions.Asynchronous);
                using var content = new MemoryStream();
                await file.CopyToAsync(content);
                return content.ToArray();
            }
            catch (IOException ex) when (ex.HResult is unchecked((int)0x80070020) or unchecked((int)0x80070021))
            {
                if (attempt == maxAttempts)
                    throw;

                await Task.Delay(50);
            }
        }
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
