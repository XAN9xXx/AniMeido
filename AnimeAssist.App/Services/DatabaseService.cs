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
                if (await GetSchemaVersionAsync(connection) is > 0 and < 7)
                {
                    await BackupAsync(throwOnFailure: true);
                }
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
                if (version < 5)
                {
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS anime_archives(
                            AnimeId INTEGER PRIMARY KEY,
                            TitleSnapshot TEXT NOT NULL,
                            PersonalRating REAL NULL CHECK(
                                PersonalRating IS NULL OR
                                (PersonalRating >= 0.5 AND
                                 PersonalRating <= 10.0 AND
                                 PersonalRating * 2 =
                                    CAST(PersonalRating * 2 AS INTEGER))),
                            SummaryNote TEXT NOT NULL DEFAULT '',
                            CreatedAt TEXT NOT NULL,
                            UpdatedAt TEXT NOT NULL
                        );

                        CREATE TABLE IF NOT EXISTS archive_entries(
                            EntryId TEXT PRIMARY KEY,
                            AnimeId INTEGER NOT NULL,
                            OccurredAt TEXT NOT NULL,
                            EpisodeNumber INTEGER NULL,
                            Body TEXT NOT NULL,
                            CreatedAt TEXT NOT NULL,
                            UpdatedAt TEXT NOT NULL,
                            FOREIGN KEY(AnimeId)
                                REFERENCES anime_archives(AnimeId)
                                ON DELETE CASCADE
                        );
                        CREATE INDEX IF NOT EXISTS
                            IX_archive_entries_anime_time
                            ON archive_entries(AnimeId, OccurredAt);

                        CREATE TABLE IF NOT EXISTS personal_tags(
                            TagId INTEGER PRIMARY KEY AUTOINCREMENT,
                            Name TEXT NOT NULL COLLATE NOCASE UNIQUE
                        );
                        CREATE TABLE IF NOT EXISTS anime_personal_tags(
                            AnimeId INTEGER NOT NULL,
                            TagId INTEGER NOT NULL,
                            PRIMARY KEY(AnimeId, TagId),
                            FOREIGN KEY(AnimeId)
                                REFERENCES anime_archives(AnimeId)
                                ON DELETE CASCADE,
                            FOREIGN KEY(TagId)
                                REFERENCES personal_tags(TagId)
                                ON DELETE CASCADE
                        );

                        CREATE TABLE IF NOT EXISTS screenshots(
                            ScreenshotId TEXT PRIMARY KEY,
                            FilePath TEXT NOT NULL,
                            Sha256 TEXT NOT NULL,
                            CapturedAt TEXT NOT NULL,
                            WindowTitle TEXT NOT NULL,
                            ProcessName TEXT NOT NULL,
                            Width INTEGER NOT NULL,
                            Height INTEGER NOT NULL,
                            AnimeId INTEGER NULL,
                            AnimeTitle TEXT NULL,
                            EpisodeNumber INTEGER NULL,
                            PlaybackPositionSeconds REAL NULL,
                            ContextNote TEXT NOT NULL DEFAULT ''
                        );
                        CREATE INDEX IF NOT EXISTS
                            IX_screenshots_anime_time
                            ON screenshots(AnimeId, CapturedAt);

                        CREATE TABLE IF NOT EXISTS screenshot_personal_tags(
                            ScreenshotId TEXT NOT NULL,
                            TagId INTEGER NOT NULL,
                            PRIMARY KEY(ScreenshotId, TagId),
                            FOREIGN KEY(ScreenshotId)
                                REFERENCES screenshots(ScreenshotId)
                                ON DELETE CASCADE,
                            FOREIGN KEY(TagId)
                                REFERENCES personal_tags(TagId)
                                ON DELETE CASCADE
                        );

                        CREATE TABLE IF NOT EXISTS manual_watch_events(
                            EventId TEXT PRIMARY KEY,
                            AnimeId INTEGER NOT NULL,
                            TitleSnapshot TEXT NOT NULL,
                            OccurredAt TEXT NOT NULL,
                            EpisodeFrom INTEGER NOT NULL,
                            EpisodeTo INTEGER NOT NULL,
                            DurationMinutes INTEGER NULL,
                            Note TEXT NOT NULL DEFAULT '',
                            CreatedAt TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS
                            IX_manual_watch_events_anime_time
                            ON manual_watch_events(AnimeId, OccurredAt);

                        CREATE TABLE IF NOT EXISTS tracking_events(
                            EventId TEXT PRIMARY KEY,
                            AnimeId INTEGER NOT NULL,
                            PreviousStatus INTEGER NULL,
                            NewStatus INTEGER NOT NULL,
                            ChangedAt TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS
                            IX_tracking_events_anime_time
                            ON tracking_events(AnimeId, ChangedAt);

                        PRAGMA user_version = 5;
                        """;
                    await cmd.ExecuteNonQueryAsync();
                    version = 5;
                }
                if (version < 6)
                {
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS
                            recommendation_feature_preferences(
                                FeatureKind INTEGER NOT NULL,
                                FeatureKey TEXT NOT NULL COLLATE NOCASE,
                                DisplayName TEXT NOT NULL,
                                Adjustment INTEGER NOT NULL CHECK(
                                    Adjustment IN (-1, 1)),
                                UpdatedAt TEXT NOT NULL,
                                PRIMARY KEY(FeatureKind, FeatureKey)
                            );

                        CREATE TABLE IF NOT EXISTS
                            recommendation_hidden_anime(
                                AnimeId INTEGER PRIMARY KEY,
                                TitleSnapshot TEXT NOT NULL,
                                HiddenAt TEXT NOT NULL
                            );

                        PRAGMA user_version = 6;
                        """;
                    await cmd.ExecuteNonQueryAsync();
                    version = 6;
                }
                if (version < 7)
                {
                    cmd.CommandText = """
                        CREATE TABLE IF NOT EXISTS external_change_receipts(
                            ChangeId TEXT PRIMARY KEY,
                            SourceId TEXT NOT NULL,
                            PayloadHash TEXT NOT NULL,
                            Result TEXT NOT NULL,
                            AppliedAt TEXT NOT NULL
                        );
                        CREATE INDEX IF NOT EXISTS
                            IX_external_change_receipts_source_time
                            ON external_change_receipts(SourceId, AppliedAt);

                        PRAGMA user_version = 7;
                        """;
                    await cmd.ExecuteNonQueryAsync();
                    version = 7;
                }
                tx.Commit();
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        private static async Task<int> GetSchemaVersionAsync(
            SqliteConnection connection)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            return Convert.ToInt32(await command.ExecuteScalarAsync());
        }
    }
}
