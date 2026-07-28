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
                await CreateTablesAsync(connection);
                await RunMigrationsAsync(connection);
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

        public async Task BackupAsync()
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
            }
#pragma warning restore CA1031
        }

        public async Task<bool> TryRestoreFromBackupAsync()
        {
            var backups = Directory.GetFiles(BackupDir, "AniMeido-*.db")
                .OrderByDescending(f => f).ToList();
            foreach (var backup in backups)
            {
                try
                {
                    using var test = new SqliteConnection(
                        new SqliteConnectionStringBuilder { DataSource = backup }.ToString());
                    await test.OpenAsync();
                    var cmd = test.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master";
                    await cmd.ExecuteScalarAsync();
                    test.Close();
                    DeleteSqliteSidecarFiles(DbPath);
                    File.Copy(backup, DbPath, overwrite: true);
                    using var connection = await _dbFactory.OpenAsync();
                    await CreateTablesAsync(connection);
                    await RunMigrationsAsync(connection);
                    var verifyCmd = connection.CreateCommand();
                    verifyCmd.CommandText = "PRAGMA integrity_check";
                    var result = await verifyCmd.ExecuteScalarAsync();
                    if (result?.ToString() != "ok")
                    {
                        Serilog.Log.Warning("Database integrity check after restore failed: {Result}", result);
                        continue;
                    }
                    return true;
                }
#pragma warning disable CA1031 // 备份损坏时继续尝试下一个
                catch
                {
                    try { File.Delete(backup); } catch (IOException) { }
                }
#pragma warning restore CA1031
            }
            return false;
        }

        private static void DeleteSqliteSidecarFiles(string dbPath)
        {
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            {
                var path = dbPath + suffix;
                try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
            }
        }

        private async Task CreateTablesAsync(SqliteConnection connection)
        {
            var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS tracking(AnimeID INTEGER PRIMARY KEY, Status INTEGER NOT NULL, UpdatedAt TEXT NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS cache(CacheKey TEXT PRIMARY KEY, Data TEXT NOT NULL, ExpiresAt TEXT NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS browse_history(AnimeID INTEGER PRIMARY KEY, TitleSnapshot TEXT, LastViewedAt TEXT NOT NULL, ViewCount INTEGER NOT NULL DEFAULT 1)";
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS saved_tags(TagName TEXT NOT NULL PRIMARY KEY)";
            await cmd.ExecuteNonQueryAsync();
        }

        private async Task RunMigrationsAsync(SqliteConnection connection)
        {
            using var tx = connection.BeginTransaction();
            try
            {
                var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "PRAGMA user_version";
                var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                if (version < 1)
                {
                    cmd.CommandText = "PRAGMA user_version = 1";
                    await cmd.ExecuteNonQueryAsync();
                    version = 1;
                }
                if (version < 2)
                {
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS saved_tags(AnimeId INTEGER NOT NULL, TagName TEXT NOT NULL, PRIMARY KEY (AnimeId, TagName))";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "PRAGMA user_version = 2";
                    await cmd.ExecuteNonQueryAsync();
                    version = 2;
                }
                if (version < 3)
                {
                    cmd.CommandText = "ALTER TABLE saved_tags RENAME TO saved_tags_old";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "CREATE TABLE saved_tags(TagName TEXT NOT NULL PRIMARY KEY)";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "INSERT OR IGNORE INTO saved_tags(TagName) SELECT DISTINCT TagName FROM saved_tags_old WHERE TagName IS NOT NULL AND TRIM(TagName) <> ''";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "DROP TABLE saved_tags_old";
                    await cmd.ExecuteNonQueryAsync();
                    cmd.CommandText = "PRAGMA user_version = 3";
                    await cmd.ExecuteNonQueryAsync();
                    version = 3;
                }
                if (version < 4)
                {
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS anime_plans(
                            AnimeId INTEGER PRIMARY KEY,
                            TitleSnapshot TEXT NOT NULL,
                            Priority INTEGER NOT NULL DEFAULT 1,
                            TargetStartDate TEXT NULL,
                            SortOrder INTEGER NOT NULL DEFAULT 0,
                            CreatedAt TEXT NOT NULL,
                            UpdatedAt TEXT NOT NULL,
                            StartedAt TEXT NULL,
                            ArchivedAt TEXT NULL
                        );

                        CREATE TABLE IF NOT EXISTS plan_reminders(
                            ReminderId TEXT PRIMARY KEY,
                            AnimeId INTEGER NOT NULL,
                            Kind INTEGER NOT NULL,
                            RelativeDays INTEGER NULL,
                            TimeOfDay TEXT NULL,
                            AbsoluteAt TEXT NULL,
                            ScheduledFor TEXT NOT NULL,
                            State INTEGER NOT NULL DEFAULT 0,
                            CatchUpSentAt TEXT NULL,
                            HandledAt TEXT NULL,
                            FOREIGN KEY(AnimeId)
                                REFERENCES anime_plans(AnimeId)
                                ON DELETE CASCADE
                        );

                        CREATE INDEX IF NOT EXISTS
                            IX_plan_reminders_anime_state
                            ON plan_reminders(AnimeId, State);
                        CREATE INDEX IF NOT EXISTS
                            IX_plan_reminders_schedule
                            ON plan_reminders(State, ScheduledFor);

                        CREATE TABLE IF NOT EXISTS anime_progress(
                            AnimeId INTEGER PRIMARY KEY,
                            CurrentEpisode INTEGER NOT NULL,
                            PositionSeconds REAL NOT NULL,
                            DurationSeconds REAL NOT NULL,
                            LastWatchedAt TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS episode_progress(
                            AnimeId INTEGER NOT NULL,
                            EpisodeNumber INTEGER NOT NULL,
                            PositionSeconds REAL NOT NULL,
                            DurationSeconds REAL NOT NULL,
                            IsCompleted INTEGER NOT NULL,
                            LastWatchedAt TEXT NOT NULL,
                            PRIMARY KEY(AnimeId, EpisodeNumber)
                        );

                        CREATE TABLE IF NOT EXISTS watch_sessions(
                            EventId TEXT PRIMARY KEY,
                            AnimeId INTEGER NOT NULL,
                            EpisodeNumber INTEGER NOT NULL,
                            PositionSeconds REAL NOT NULL,
                            DurationSeconds REAL NOT NULL,
                            IsCompleted INTEGER NOT NULL,
                            ObservedAt TEXT NOT NULL
                        );

                        CREATE INDEX IF NOT EXISTS
                            IX_watch_sessions_anime_observed
                            ON watch_sessions(AnimeId, ObservedAt);

                        CREATE TABLE IF NOT EXISTS smart_lists(
                            Id TEXT PRIMARY KEY,
                            Name TEXT NOT NULL,
                            SchemaVersion INTEGER NOT NULL,
                            RuleJson TEXT NOT NULL,
                            SortJson TEXT NULL,
                            CreatedAt TEXT NOT NULL,
                            UpdatedAt TEXT NOT NULL
                        );

                        PRAGMA user_version = 4;
                        """;
                    await cmd.ExecuteNonQueryAsync();
                    version = 4;
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }
}
