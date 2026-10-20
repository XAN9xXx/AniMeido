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
        private readonly InstalledAppContext? _installedApp;

        public string DbPath { get; }

        public string LogDir { get; }

        public string BackupDir { get; }

        private const int MaxBackups = 10;
        private const int MaxUpdateBackups = 2;
        private const string PublicIdMigrationBackupKey = "public-id-migration-backup";

        private string UpdateBackupDir => Path.Combine(BackupDir, "update");

        private const int RestoreStepMaxAttempts = 20;
        private const int RestoreStepRetryDelayMilliseconds = 100;
        private const int RestoreStepRetryBudgetMilliseconds = 2000;
        private const int RestoreSqliteTimeoutSeconds = 1;

        public DatabaseService(SqliteConnectionFactory dbFactory, IAppDataPaths paths)
            : this(dbFactory, paths, null)
        {
        }

        public DatabaseService(SqliteConnectionFactory dbFactory, IAppDataPaths paths,
            InstalledAppContext? installedApp = null)
        {
            _dbFactory = dbFactory;
            _installedApp = installedApp;
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
                var previousVersion = await DatabaseSchema.GetSchemaVersionAsync(connection);
                if (previousVersion is > 0 and < DatabaseSchema.CurrentVersion
                    && !await HasPublicIdMigrationBackupAsync(connection, previousVersion))
                {
                    var backupPath = await BackupCoreAsync(throwOnFailure: true, previousVersion);
                    if (backupPath is not null)
                        await SetConfigAsync(connection, PublicIdMigrationBackupKey, backupPath);
                    if (_installedApp?.IsInstalled == true && backupPath is not null)
                    {
                        try
                        {
                            _installedApp.RecordMigration(backupPath);
                        }
#pragma warning disable CA1031 // 备份已完成；安装状态记录失败不能阻止数据库迁移。
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "无法记录数据库迁移备份，仍继续迁移；启动器可能无法自动恢复数据。");
                        }
#pragma warning restore CA1031
                    }
                }
                await DatabaseSchema.RunMigrationsAsync(connection);
                try
                {
                    await DatabaseSchema.UpgradeEmptyDatabaseAsync(connection);
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is not (11 or 26))
                {
                    // 空库也可能在 v9 事务中失败；留在 v8，由主窗口的迁移阻塞页重试。
                    Serilog.Log.Warning(ex, "Empty database public ID migration deferred to startup retry");
                }
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

        public Task BackupAsync(bool throwOnFailure = false)
            => BackupCoreAsync(throwOnFailure);

        private async Task<string?> BackupCoreAsync(bool throwOnFailure, int? previousVersion = null)
        {
            try
            {
                var isUpdateBackup = previousVersion.HasValue;
                var backupDirectory = isUpdateBackup ? UpdateBackupDir : BackupDir;
                Directory.CreateDirectory(backupDirectory);
                using (var checkpointCmd = await _dbFactory.OpenAsync())
                {
                    using var cmd = checkpointCmd.CreateCommand();
                    cmd.CommandText = "PRAGMA wal_checkpoint(FULL)";
                    await cmd.ExecuteNonQueryAsync();
                }
                var suffix = Guid.NewGuid().ToString("N")[..8];
                var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                var fileName = isUpdateBackup
                    ? $"AniMeido-before-v{previousVersion}-to-v{DatabaseSchema.CurrentVersion}-{timestamp}-{suffix}.db"
                    : $"AniMeido-{timestamp}-{suffix}.db";
                var backupPath = Path.Combine(backupDirectory, fileName);
                using (var source = await _dbFactory.OpenAsync())
                using (var dest = new SqliteConnection(
                    new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString()))
                {
                    await dest.OpenAsync();
                    source.BackupDatabase(dest);
                }
                // 滚动备份只枚举顶层，不能清理 update 或 corrupt 子目录。
                var backups = (isUpdateBackup
                    ? GetUpdateBackups()
                    : Directory.GetFiles(BackupDir, "AniMeido-*.db", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(f => f)).ToList();
                var maxBackups = isUpdateBackup ? MaxUpdateBackups : MaxBackups;
                while (backups.Count > maxBackups)
                {
                    var oldest = backups.Last();
                    try { File.Delete(oldest); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Serilog.Log.Warning(ex, "Failed to delete old database backup {Backup}.", oldest);
                    }
                    backups.RemoveAt(backups.Count - 1);
                }
                return backupPath;
            }
#pragma warning disable CA1031 // 备份失败不影响主流程
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Database backup failed.");
                if (throwOnFailure)
                {
                    throw;
                }
                return null;
            }
#pragma warning restore CA1031
        }

        public async Task<bool> TryRestoreFromBackupAsync()
        {
            SqliteConnection.ClearAllPools();
            await foreach (var backup in EnumerateRestoreBackupsAsync())
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
#pragma warning disable CA1031 // 非暂时性的补表/迁移失败说明该备份不可用；暂时性访问失败必须中止，不能退回旧备份
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

        // 停在 v8 等待联网迁移时，库里记着本轮更新前备份且文件仍在，就不再备份，
        // 否则每次启动都会滚掉真正的更新前备份并改写启动器的恢复路径。备份本身不含此标记，从备份恢复的库会重新备份。
        private static async Task<bool> HasPublicIdMigrationBackupAsync(SqliteConnection connection, int previousVersion)
        {
            // 只复用停在 v8 的 C2a 备份。v9 -> v10 必须另做离线更新前备份。
            if (previousVersion != DatabaseSchema.OfflineVersion)
                return false;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key=@key";
            command.Parameters.AddWithValue("@key", PublicIdMigrationBackupKey);
            return await command.ExecuteScalarAsync() is string path && File.Exists(path);
        }

        private static async Task SetConfigAsync(SqliteConnection connection, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            await command.ExecuteNonQueryAsync();
        }

        private IEnumerable<string> GetUpdateBackups()
            => Directory.GetFiles(UpdateBackupDir, "AniMeido-before-*.db", SearchOption.TopDirectoryOnly)
                // 文件名前面的结构版本不表示新旧；最后三段是日期、时间与随机后缀。
                .OrderByDescending(path => string.Join("-",
                    Path.GetFileNameWithoutExtension(path).Split('-').TakeLast(3)), StringComparer.Ordinal);

        private async IAsyncEnumerable<string> EnumerateRestoreBackupsAsync()
        {
            string[] backups = [];
            await RetryRestoreStepAsync(() =>
            {
                backups = Directory.GetFiles(BackupDir, "AniMeido-*.db", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f).ToArray();
                return Task.CompletedTask;
            }, "Enumerate backups", BackupDir);
            foreach (var backup in backups)
                yield return backup;

            // 只有顶层候选全部不可用才枚举更新备份；目录不存在属于正常的旧安装状态。
            await RetryRestoreStepAsync(() =>
            {
                try { backups = GetUpdateBackups().ToArray(); }
                catch (DirectoryNotFoundException) { backups = []; }
                return Task.CompletedTask;
            }, "Enumerate update backups", UpdateBackupDir);
            foreach (var backup in backups)
                yield return backup;
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
