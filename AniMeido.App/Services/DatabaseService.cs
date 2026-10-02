using AniMeido.Contracts;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.App.Services
{
    /// <summary>
    /// 数据库服务：初始化、自动备份、版本迁移、损坏检测。
    /// </summary>
    public class DatabaseService
    {
        private readonly SqliteConnectionFactory _dbFactory;

        /// <summary>数据库文件路径</summary>
        public string DbPath { get; }

        /// <summary>日志目录路径</summary>
        public string LogDir { get; }

        /// <summary>备份目录路径</summary>
        public string BackupDir { get; }

        /// <summary>最大备份保留数</summary>
        private const int MaxBackups = 10;

        public DatabaseService(SqliteConnectionFactory dbFactory, IAppDataPaths paths)
        {
            _dbFactory = dbFactory;
            DbPath = dbFactory.DatabasePath;
            LogDir = paths.LogDirectory;
            BackupDir = paths.BackupDirectory;
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(BackupDir);
        }

        public async Task InitializeAsync()
        {
            try
            {
                using var connection = await _dbFactory.OpenAsync();
                using (var pragmaCmd = connection.CreateCommand())
                {
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL";
                    await pragmaCmd.ExecuteNonQueryAsync();
                }
                await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
                await CreateConfigTableAsync(connection);
                await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
                if (await DatabaseSchema.GetSchemaVersionAsync(connection)
                    is > 0 and < DatabaseSchema.CurrentVersion)
                {
                    await BackupAsync(throwOnFailure: true);
                }
                await DatabaseSchema.RunMigrationsAsync(connection);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
            {
                var restored = await TryRestoreFromBackupAsync();
                if (!restored)
                    throw new InvalidOperationException("数据库文件已损坏，且没有可用备份。请手动删除数据库文件后重启应用。", ex);
                return;
            }
            catch (IOException) when (!File.Exists(DbPath))
            {
            }
            _ = BackupAsync();
        }

        public async Task BackupAsync(bool throwOnFailure = false)
        {
            try
            {
                using (var checkpointCmd = await _dbFactory.OpenAsync())
                {
                    using var cmd = checkpointCmd.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
                    await cmd.ExecuteNonQueryAsync();
                }
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var backupPath = Path.Combine(BackupDir, $"AniMeido-{timestamp}-{suffix}.db");
                using var source = await _dbFactory.OpenAsync();
                using var dest = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
                await dest.OpenAsync();
                source.BackupDatabase(dest);
                var backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                    .OrderByDescending(f => f).ToList();
                while (backups.Count > MaxBackups)
                {
                    try { File.Delete(backups.Last()); } catch (IOException) { }
                    backups.RemoveAt(backups.Count - 1);
                }
            }
#pragma warning disable CA1031 // 备份失败不影响主流程
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Database backup failed.");
                if (throwOnFailure)
                {
                    throw;
                }
            }
#pragma warning restore CA1031
        }

        public async Task<bool> TryRestoreFromBackupAsync()
        {
            SqliteConnection.ClearAllPools();
            var backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                .OrderByDescending(f => f).ToList();
            foreach (var backup in backups)
            {
                var temporaryPath = Path.Combine(
                    Path.GetDirectoryName(DbPath)!, $".AniMeido-restore-{Guid.NewGuid():N}.db");
                try
                {
                    using (var test = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = backup,
                        Mode = SqliteOpenMode.ReadOnly,
                        Pooling = false,
                    }.ToString()))
                    {
                        await test.OpenAsync();
                        if (!await CheckIntegrityAsync(test))
                            continue;
                    }

                    File.Copy(backup, temporaryPath);
                    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = temporaryPath,
                        Mode = SqliteOpenMode.ReadWrite,
                        Pooling = false,
                    }.ToString()))
                    {
                        await connection.OpenAsync();
                        await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
                        await CreateConfigTableAsync(connection);
                        await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
                        await DatabaseSchema.RunMigrationsAsync(connection);
                        if (!await CheckIntegrityAsync(connection))
                            continue;

                        // 迁移的 WAL 写入必须在移动临时主文件之前落盘。
                        using var checkpoint = connection.CreateCommand();
                        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                        await checkpoint.ExecuteNonQueryAsync();
                    }

                    ReplaceDatabase(temporaryPath);
                    return true;
                }
#pragma warning disable CA1031 // 单份备份恢复失败时保留备份并继续尝试下一份
                catch (Exception ex)
                {
                    Serilog.Log.Warning(ex, "Database restore from {Backup} failed.", backup);
                }
#pragma warning restore CA1031
                finally
                {
                    DeleteRestoreTemporaryFiles(temporaryPath);
                }
            }
            return false;
        }

        private static async Task<bool> CheckIntegrityAsync(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check";
            var result = await command.ExecuteScalarAsync();
            if (result?.ToString() == "ok")
                return true;

            Serilog.Log.Warning("Database integrity check failed for {Database}: {Result}", connection.DataSource, result);
            return false;
        }

        private void ReplaceDatabase(string temporaryPath)
        {
            var corruptDirectory = Path.Combine(BackupDir, "corrupt");
            Directory.CreateDirectory(corruptDirectory);
            var corruptPath = Path.Combine(corruptDirectory,
                $"AniMeido-corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.db");
            var moved = new List<(string Original, string Archived)>();
            try
            {
                foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                {
                    var original = DbPath + suffix;
                    if (!File.Exists(original))
                        continue;

                    var archived = corruptPath + suffix;
                    File.Move(original, archived);
                    moved.Add((original, archived));
                }

                File.Move(temporaryPath, DbPath);
            }
#pragma warning disable CA1031 // 替换失败时尽力将主库及已移动的 sidecar 恢复原位
            catch
            {
                foreach (var (original, archived) in moved.AsEnumerable().Reverse())
                {
                    try { File.Move(archived, original); }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to return {Archived} to {Original} after restore failure.", archived, original);
                    }
                }

                throw;
            }
#pragma warning restore CA1031
        }

        private static void DeleteRestoreTemporaryFiles(string dbPath)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var path = dbPath + suffix;
                try { File.Delete(path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Serilog.Log.Warning(ex, "Restore temporary database cleanup failed for {Path}.", path);
                }
            }
        }

        private static async Task CreateConfigTableAsync(
            SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
