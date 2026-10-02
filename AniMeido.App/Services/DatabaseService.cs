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

        private const int RestoreStepMaxAttempts = 20;
        private const int RestoreStepRetryDelayMilliseconds = 100;
        private const int RestoreStepRetryBudgetMilliseconds = 2000;
        private const int RestoreSqliteTimeoutSeconds = 1;

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
                try
                {
                    var restored = await TryRestoreFromBackupAsync();
                    if (!restored)
                        throw new InvalidOperationException("数据库文件已损坏，且没有可用备份。请手动删除数据库文件后重启应用。", ex);
                    return;
                }
                catch (DatabaseRestoreUnavailableException restoreException)
                {
                    throw new InvalidOperationException(
                        "数据库文件已损坏；备份文件暂时被其他程序占用，未执行恢复。请稍后重启 AniMeido 再试。",
                        restoreException);
                }
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
            string[] backups = [];
            await RetryRestoreStepAsync(() =>
            {
                backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                    .OrderByDescending(f => f).ToArray();
                return Task.CompletedTask;
            }, "Enumerate backups", BackupDir);
            foreach (var backup in backups)
            {
                var temporaryPath = Path.Combine(
                    Path.GetDirectoryName(DbPath)!, $".AniMeido-restore-{Guid.NewGuid():N}.db");
                var restored = false;
                DatabaseRestoreUnavailableException? restoreFailure = null;
                try
                {
                    var validBackup = false;
                    try
                    {
                        await RetryRestoreStepAsync(async () =>
                        {
                            using var test = new SqliteConnection(new SqliteConnectionStringBuilder
                            {
                                DataSource = backup,
                                Mode = SqliteOpenMode.ReadOnly,
                                Pooling = false,
                                DefaultTimeout = RestoreSqliteTimeoutSeconds,
                            }.ToString());
                            await test.OpenAsync();
                            validBackup = await CheckIntegrityAsync(test);
                        }, "Open and check backup", backup);
                    }
                    catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
                    {
                        Serilog.Log.Warning(ex, "Database backup {Backup} is corrupt or not a database.", backup);
                        continue;
                    }
                    if (!validBackup)
                        continue;

                    await RetryRestoreStepAsync(() =>
                    {
                        // 一次复制失败可能留下部分临时文件；下一次只覆盖本次创建的临时路径。
                        File.Copy(backup, temporaryPath, overwrite: true);
                        return Task.CompletedTask;
                    }, "Copy backup", $"{backup} -> {temporaryPath}");
                    var validTemporaryDatabase = false;
                    try
                    {
                        await RetryRestoreStepAsync(async () =>
                        {
                            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                            {
                                DataSource = temporaryPath,
                                Mode = SqliteOpenMode.ReadWrite,
                                Pooling = false,
                                DefaultTimeout = RestoreSqliteTimeoutSeconds,
                            }.ToString());
                            await connection.OpenAsync();
                            await DatabaseSchema.CreateInitialBusinessTablesBeforeConfigAsync(connection);
                            await CreateConfigTableAsync(connection);
                            await DatabaseSchema.CreateInitialBusinessTablesAfterConfigAsync(connection);
                            await DatabaseSchema.RunMigrationsAsync(connection);
                            validTemporaryDatabase = await CheckIntegrityAsync(connection);
                            if (!validTemporaryDatabase)
                                return;

                            // 迁移的 WAL 写入必须在移动临时主文件之前落盘。
                            using var checkpoint = connection.CreateCommand();
                            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                            if (Convert.ToInt32(await checkpoint.ExecuteScalarAsync()) != 0)
                                throw new SqliteException("Restore database checkpoint is busy.", 5);
                        }, "Prepare and check temporary database", temporaryPath);
                    }
#pragma warning disable CA1031 // 非暂时性的补表/迁移失败说明该备份不可用；B 类必须中止，不能退回旧备份
                    catch (Exception ex) when (ex is not DatabaseRestoreUnavailableException && !IsRestoreUnavailable(ex))
                    {
                        Serilog.Log.Warning(ex, "Database backup {Backup} could not be migrated.", backup);
                        continue;
                    }
#pragma warning restore CA1031
                    if (!validTemporaryDatabase)
                        continue;

                    // 最终移动是提交点：先清理临时 sidecar，不能安装成功后才因清理失败报告未恢复。
                    await DeleteRestoreTemporaryFilesAsync(temporaryPath, includeMainFile: false);
                    await ReplaceDatabaseAsync(temporaryPath);
                    restored = true;
                    return true;
                }
                catch (DatabaseRestoreUnavailableException ex)
                {
                    restoreFailure = ex;
                    throw;
                }
                finally
                {
                    if (!restored)
                    {
                        try
                        {
                            await DeleteRestoreTemporaryFilesAsync(temporaryPath);
                        }
                        catch (DatabaseRestoreUnavailableException) when (restoreFailure is not null)
                        {
                            // 清理步骤已记录 Warning；保留中止恢复的原始异常，不让清理异常覆盖它。
                        }
                    }
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

        private async Task ReplaceDatabaseAsync(string temporaryPath)
        {
            var corruptDirectory = Path.Combine(BackupDir, "corrupt");
            await RetryRestoreStepAsync(() =>
            {
                Directory.CreateDirectory(corruptDirectory);
                return Task.CompletedTask;
            }, "Create corrupt archive directory", corruptDirectory);
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
                    await RetryRestoreStepAsync(() =>
                    {
                        File.Move(original, archived);
                        return Task.CompletedTask;
                    }, "Archive original database file", $"{original} -> {archived}");
                    moved.Add((original, archived));
                }

                await RetryRestoreStepAsync(() =>
                {
                    File.Move(temporaryPath, DbPath);
                    return Task.CompletedTask;
                }, "Install restored database", $"{temporaryPath} -> {DbPath}");
            }
#pragma warning disable CA1031 // 替换失败时逐项重试回滚；某项回滚仍失败不能阻止其他项恢复原位
            catch
            {
                foreach (var (original, archived) in moved.AsEnumerable().Reverse())
                {
                    try
                    {
                        await RetryRestoreStepAsync(() =>
                        {
                            File.Move(archived, original);
                            return Task.CompletedTask;
                        }, "Roll back archived database file", $"{archived} -> {original}");
                    }
                    catch (DatabaseRestoreUnavailableException)
                    {
                        // 重试用尽已经记录 Warning；继续回滚其余文件，最后重新抛出替换失败的异常。
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "Failed to return {Archived} to {Original} after restore failure.", archived, original);
                    }
                }

                throw;
            }
#pragma warning restore CA1031
        }

        private static async Task DeleteRestoreTemporaryFilesAsync(string dbPath, bool includeMainFile = true)
        {
            DatabaseRestoreUnavailableException? cleanupFailure = null;
            var suffixes = includeMainFile ? new[] { "", "-wal", "-shm", "-journal" } : new[] { "-wal", "-shm", "-journal" };
            foreach (var suffix in suffixes)
            {
                var path = dbPath + suffix;
                try
                {
                    await RetryRestoreStepAsync(() =>
                    {
                        File.Delete(path);
                        return Task.CompletedTask;
                    }, "Delete restore temporary file", path);
                }
                catch (DatabaseRestoreUnavailableException ex)
                {
                    cleanupFailure ??= ex;
                }
            }
            if (cleanupFailure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }

        private static bool IsRestoreUnavailable(Exception exception)
            => exception is IOException or UnauthorizedAccessException
                || exception is SqliteException { SqliteErrorCode: 5 or 6 or 10 or 14 };

        private static async Task RetryRestoreStepAsync(Func<Task> operation, string step, string path)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await operation();
                    return;
                }
                catch (Exception ex) when (IsRestoreUnavailable(ex))
                {
                    var remaining = RestoreStepRetryBudgetMilliseconds - elapsed.ElapsedMilliseconds;
                    if (attempt == RestoreStepMaxAttempts || remaining <= 0)
                    {
                        Serilog.Log.Warning(ex, "Database restore step {Step} for {Path} failed after {Attempts} attempts in {ElapsedMilliseconds} ms.",
                            step, path, attempt, elapsed.ElapsedMilliseconds);
                        throw new DatabaseRestoreUnavailableException(
                            $"Database restore step '{step}' for '{path}' is temporarily unavailable.", ex);
                    }

                    await Task.Delay((int)Math.Min(RestoreStepRetryDelayMilliseconds, remaining));
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
