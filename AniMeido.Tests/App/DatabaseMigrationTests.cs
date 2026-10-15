using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests
{
    public class DatabaseMigrationTests : DbTestBase
    {
        private new async Task RunProductionMigrationAsync()
        {
            var db = new DatabaseService(DbFactory, Paths);
            await db.InitializeAsync();
        }

        [Fact]
        public async Task FullMigration_CreatesAllTables()
        {
            await RunProductionMigrationAsync();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(DatabaseSchema.CurrentVersion, version);

            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            var tables = new List<string>();
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tables.Add(reader.GetString(0));

            Assert.Contains("tracking", tables);
            Assert.Contains("cache", tables);
            Assert.Contains("config", tables);
            Assert.Contains("saved_tags", tables);
            Assert.Contains("browse_history", tables);
            Assert.Contains("anime_plans", tables);
            Assert.Contains("plan_reminders", tables);
            Assert.Contains("anime_progress", tables);
            Assert.Contains("episode_progress", tables);
            Assert.Contains("watch_sessions", tables);
            Assert.Contains("smart_lists", tables);
            Assert.Contains("anime_archives", tables);
            Assert.Contains("archive_entries", tables);
            Assert.Contains("personal_tags", tables);
            Assert.Contains("anime_personal_tags", tables);
            Assert.Contains("screenshots", tables);
            Assert.Contains("screenshot_personal_tags", tables);
            Assert.Contains("manual_watch_events", tables);
            Assert.Contains("tracking_events", tables);
            Assert.Contains("recommendation_feature_preferences", tables);
            Assert.Contains("recommendation_hidden_anime", tables);
            Assert.Contains("external_change_receipts", tables);
        }

        [Fact]
        public async Task Migration_FromV1ToV2_PreservesExistingData()
        {
            // 模拟 v1 数据库
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();

                cmd.CommandText = """
                    CREATE TABLE tracking(
                        AnimeID   INTEGER PRIMARY KEY,
                        Status    INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    CREATE TABLE cache(
                        CacheKey  TEXT PRIMARY KEY,
                        Data      TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    CREATE TABLE config(
                        Key   TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "INSERT INTO tracking (AnimeID, Status, UpdatedAt) VALUES (1, 1, '2024-01-01')";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA user_version = 1";
                await cmd.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verifyConn.OpenAsync();
            var check = verifyConn.CreateCommand();
            check.CommandText = "SELECT Status FROM tracking WHERE AnimeID = 1";
            var status = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(1, status);

            var tableCmd = verifyConn.CreateCommand();
            tableCmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='saved_tags'";
            var tableExists = await tableCmd.ExecuteScalarAsync();
            Assert.NotNull(tableExists);
        }

        [Fact]
        public async Task Migration_FromV2ToV3_PreservesDistinctTagNames()
        {
            // 模拟 v2 数据库（saved_tags 有 AnimeId + TagName）
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await conn.OpenAsync();
                var cmd = conn.CreateCommand();

                cmd.CommandText = """
                    CREATE TABLE tracking(
                        AnimeID   INTEGER PRIMARY KEY,
                        Status    INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    CREATE TABLE cache(
                        CacheKey  TEXT PRIMARY KEY,
                        Data      TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    CREATE TABLE config(
                        Key   TEXT PRIMARY KEY,
                        Value TEXT NOT NULL
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = """
                    CREATE TABLE saved_tags(
                        AnimeId INTEGER NOT NULL,
                        TagName TEXT NOT NULL,
                        PRIMARY KEY (AnimeId, TagName)
                    )
                """;
                await cmd.ExecuteNonQueryAsync();

                // 插入旧数据（含重复 TagName）
                cmd.CommandText = "INSERT INTO saved_tags (AnimeId, TagName) VALUES (1, '原创')";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "INSERT INTO saved_tags (AnimeId, TagName) VALUES (2, '原创')";
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "INSERT INTO saved_tags (AnimeId, TagName) VALUES (3, '科幻')";
                await cmd.ExecuteNonQueryAsync();

                cmd.CommandText = "PRAGMA user_version = 2";
                await cmd.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verifyConn.OpenAsync();
            var versionCmd = verifyConn.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version";
            Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await versionCmd.ExecuteScalarAsync()));

            // 验证 Distinct TagName 被保留（"原创"只出现一次）
            var tagCmd = verifyConn.CreateCommand();
            tagCmd.CommandText = "SELECT TagName FROM saved_tags ORDER BY TagName";
            var tags = new List<string>();
            using var reader = await tagCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                tags.Add(reader.GetString(0));

            Assert.Equal(2, tags.Count);
            Assert.Contains("原创", tags);
            Assert.Contains("科幻", tags);
        }

        [Fact]
        public async Task Migration_Idempotent_RunningTwiceIsSafe()
        {
            await RunProductionMigrationAsync();
            await RunProductionMigrationAsync();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(DatabaseSchema.CurrentVersion, version);
        }

        [Fact]
        public async Task RepeatedCurrentVersionInitialization_PreservesRecordsAndConfig()
        {
            await RunProductionMigrationAsync();

            await using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(701, 3, '2026-09-08T00:00:00Z');
                    INSERT INTO config(Key, Value)
                    VALUES('fixture-key', 'fixture-value');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            await using var verify =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verify.OpenAsync();
            var command2 = verify.CreateCommand();
            command2.CommandText =
                "SELECT Status FROM tracking WHERE AnimeID = 701";
            Assert.Equal(3, Convert.ToInt32(await command2.ExecuteScalarAsync()));
            command2.CommandText =
                "SELECT Value FROM config WHERE Key = 'fixture-key'";
            Assert.Equal("fixture-value", await command2.ExecuteScalarAsync());
            command2.CommandText = "PRAGMA user_version";
            Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await command2.ExecuteScalarAsync()));
        }

        [Fact]
        public async Task Migration_FromV4_ToCurrent_BacksUpAndPreservesTracking()
        {
            using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var setupCommand = connection.CreateCommand();
                setupCommand.CommandText = HistoricalSchemas.V4 + """
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(88, 5, '2026-07-01T00:00:00Z');
                    INSERT INTO anime_plans(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt)
                    VALUES(88, '历史计划', '2026-07-01', '2026-07-01');
                    INSERT INTO plan_reminders(ReminderId, AnimeId, Kind, ScheduledFor)
                    VALUES('historical-reminder', 88, 1, '2026-07-02');
                    INSERT INTO config(Key, Value) VALUES('historical-key', 'historical-value');
                    """;
                await setupCommand.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            using var verify =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verify.OpenAsync();
            var statusCommand = verify.CreateCommand();
            statusCommand.CommandText =
                "SELECT Status FROM tracking WHERE AnimeID = 88";
            Assert.Equal(
                5,
                Convert.ToInt32(await statusCommand.ExecuteScalarAsync()));
            await AssertUpdateBackupAsync(4);
            statusCommand.CommandText = "SELECT TitleSnapshot FROM anime_plans WHERE AnimeId = 88";
            Assert.Equal("历史计划", await statusCommand.ExecuteScalarAsync());
            statusCommand.CommandText = "SELECT AnimeId FROM plan_reminders WHERE ReminderId = 'historical-reminder'";
            Assert.Equal(88L, await statusCommand.ExecuteScalarAsync());
            statusCommand.CommandText = "SELECT Value FROM config WHERE Key = 'historical-key'";
            Assert.Equal("historical-value", await statusCommand.ExecuteScalarAsync());
            await AssertCurrentSchemaAndIdempotenceAsync();
        }

        [Fact]
        public async Task Migration_FromV5_ToCurrent_PreservesTrackingAndCreatesRecommendationTables()
        {
            await using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = HistoricalSchemas.V5 + """
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(96, 1, '2026-08-01T00:00:00Z');
                    INSERT INTO anime_archives VALUES(96, '历史档案', 8, '历史摘要', '2026-08-01', '2026-08-01');
                    INSERT INTO archive_entries VALUES('historical-entry', 96, '2026-08-01', 1, '历史笔记', '2026-08-01', '2026-08-01');
                    INSERT INTO personal_tags(TagId, Name) VALUES(1, '历史标签');
                    INSERT INTO anime_personal_tags VALUES(96, 1);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            await using var verify =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verify.OpenAsync();
            var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "SELECT Status FROM tracking WHERE AnimeID = 96";
            Assert.Equal(1, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync()));
            verifyCommand.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name IN(
                    'recommendation_feature_preferences',
                    'recommendation_hidden_anime')
                """;
            Assert.Equal(2, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync()));
            await AssertUpdateBackupAsync(5);
            verifyCommand.CommandText = "SELECT SummaryNote FROM anime_archives WHERE AnimeId = 96";
            Assert.Equal("历史摘要", await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = "SELECT Body FROM archive_entries WHERE EntryId = 'historical-entry'";
            Assert.Equal("历史笔记", await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = "SELECT Name FROM personal_tags JOIN anime_personal_tags USING(TagId) WHERE AnimeId = 96";
            Assert.Equal("历史标签", await verifyCommand.ExecuteScalarAsync());
            await AssertCurrentSchemaAndIdempotenceAsync();
        }

        [Fact]
        public async Task Migration_FromV6_ToCurrent_PreservesTrackingAndCreatesReceipts()
        {
            await using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = HistoricalSchemas.V6 + """
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(622206, 3, '2026-08-01T00:00:00Z');
                    INSERT INTO recommendation_feature_preferences VALUES(0, '历史偏好', '历史偏好', 1, '2026-08-01');
                    INSERT INTO recommendation_hidden_anime VALUES(622207, '历史隐藏作品', '2026-08-01');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            await using var verify =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verify.OpenAsync();
            var verifyCommand = verify.CreateCommand();
            verifyCommand.CommandText = "PRAGMA user_version";
            Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync()));
            verifyCommand.CommandText =
                "SELECT Status FROM tracking WHERE AnimeID = 622206";
            Assert.Equal(3, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync()));
            verifyCommand.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE type = 'table' AND name = 'external_change_receipts'
                """;
            Assert.Equal(1, Convert.ToInt32(await verifyCommand.ExecuteScalarAsync()));
            await AssertUpdateBackupAsync(6);
            verifyCommand.CommandText = "SELECT Adjustment FROM recommendation_feature_preferences WHERE FeatureKey = '历史偏好'";
            Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
            verifyCommand.CommandText = "SELECT TitleSnapshot FROM recommendation_hidden_anime WHERE AnimeId = 622207";
            Assert.Equal("历史隐藏作品", await verifyCommand.ExecuteScalarAsync());
            await AssertCurrentSchemaAndIdempotenceAsync();
        }

        [Fact]
        public async Task Migration_ThreeUpdatesKeepOnlyNewestTwoByFilenameTimestamp()
        {
            var updateDirectory = Path.Combine(Paths.BackupDirectory, "update");
            Directory.CreateDirectory(updateDirectory);
            var rollbackData = Path.Combine(updateDirectory, "AniMeido-rollback-20000101.db");
            await File.WriteAllTextAsync(rollbackData, "user-owned rollback data");
            var backups = new List<string>();
            // 故意让版本号与迁移先后顺序不一致；不能按版本或文件修改时间清理。
            var oldSchemas = new[] { (6, HistoricalSchemas.V6), (4, HistoricalSchemas.V4), (5, HistoricalSchemas.V5) };
            foreach (var (version, schema) in oldSchemas)
            {
                var sourcePaths = CreateAdditionalPaths();
                var factory = new SqliteConnectionFactory(sourcePaths);
                using (var connection = await factory.OpenAsync())
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = schema;
                    await command.ExecuteNonQueryAsync();
                }

                // 每次使用独立旧结构库，共享本夹具的备份目录；不重建正在后台备份的库。
                await new DatabaseService(factory, Paths).InitializeAsync();

                var generated = Assert.Single(Directory.GetFiles(updateDirectory,
                    $"AniMeido-before-v{version}-to-v{DatabaseSchema.CurrentVersion}-*.db"));
                var renamed = Path.Combine(updateDirectory,
                    $"AniMeido-before-v{version}-to-v{DatabaseSchema.CurrentVersion}-2000010{backups.Count + 1}-010203-abcdef01.db");
                // 不清池即可移动：迁移前备份的目标连接必须已释放句柄。
                File.Move(generated, renamed);
                File.SetLastWriteTime(renamed, new DateTime(2030 - backups.Count, 1, 1));
                backups.Add(renamed);
                using var verify = await factory.OpenAsync();
                Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(verify));
            }

            var retained = Directory.GetFiles(updateDirectory, "AniMeido-before-*.db");
            Assert.Equal(2, retained.Length);
            Assert.False(File.Exists(backups[0]));
            Assert.Contains(backups[1], retained);
            Assert.Contains(backups[2], retained);
            Assert.Equal("user-owned rollback data", await File.ReadAllTextAsync(rollbackData));
        }

        [Fact]
        public async Task Migration_UpdateBackupFailurePreventsSchemaMigration()
        {
            var service = new DatabaseService(DbFactory, Paths);
            // 用文件占住 update 目录，确定性地使迁移前备份失败。
            await File.WriteAllTextAsync(Path.Combine(Paths.BackupDirectory, "update"), "not a directory");
            using (var connection = await DbFactory.OpenAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = HistoricalSchemas.V6 + """
                    INSERT INTO tracking VALUES(42, 1, '2026-09-01');
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAsync<IOException>(service.InitializeAsync);

            using var verify = await DbFactory.OpenAsync();
            Assert.Equal(6, await DatabaseSchema.GetSchemaVersionAsync(verify));
            using var check = verify.CreateCommand();
            check.CommandText = "SELECT AnimeID FROM tracking";
            Assert.Equal(42L, await check.ExecuteScalarAsync());
            check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = 'external_change_receipts'";
            Assert.Equal(0L, await check.ExecuteScalarAsync());
        }

        [Fact]
        public async Task Migration_LockedOldUpdateBackupDoesNotBlockMigration()
        {
            var service = new DatabaseService(DbFactory, Paths);
            var updateDirectory = Path.Combine(Paths.BackupDirectory, "update");
            Directory.CreateDirectory(updateDirectory);
            var oldest = Path.Combine(updateDirectory, "AniMeido-before-v5-to-v6-20000101-010203-abcdef01.db");
            var newer = Path.Combine(updateDirectory, "AniMeido-before-v4-to-v5-20000102-010203-abcdef02.db");
            await File.WriteAllTextAsync(oldest, "locked old backup");
            await File.WriteAllTextAsync(newer, "newer backup");
            using (var connection = await DbFactory.OpenAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = HistoricalSchemas.V6;
                await command.ExecuteNonQueryAsync();
            }

            using (var locked = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.None))
                await service.InitializeAsync();

            using var verify = await DbFactory.OpenAsync();
            Assert.Equal(DatabaseSchema.CurrentVersion, await DatabaseSchema.GetSchemaVersionAsync(verify));
            Assert.Equal("locked old backup", await File.ReadAllTextAsync(oldest));
            Assert.Equal("newer backup", await File.ReadAllTextAsync(newer));
            Assert.Single(Directory.GetFiles(updateDirectory,
                $"AniMeido-before-v6-to-v{DatabaseSchema.CurrentVersion}-*.db"));
        }

        private async Task AssertUpdateBackupAsync(int previousVersion)
        {
            var backup = Assert.Single(Directory.GetFiles(
                Path.Combine(Paths.BackupDirectory, "update"), "AniMeido-before-*.db"));
            Assert.Matches($@"^AniMeido-before-v{previousVersion}-to-v{DatabaseSchema.CurrentVersion}-\d{{8}}-\d{{6}}-[0-9a-f]{{8}}\.db$",
                Path.GetFileName(backup));
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = backup,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
                    Pooling = false,
                }.ToString());
            await connection.OpenAsync();
            Assert.Equal(previousVersion, await DatabaseSchema.GetSchemaVersionAsync(connection));
        }

        private async Task AssertCurrentSchemaAndIdempotenceAsync()
        {
            using var connection = await DbFactory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
            var tables = new List<string>();
            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    tables.Add(reader.GetString(0));
            }
            string[] expectedTables =
            [
                "tracking", "cache", "config", "saved_tags", "browse_history",
                "anime_plans", "plan_reminders", "anime_progress", "episode_progress",
                "watch_sessions", "smart_lists", "anime_archives", "archive_entries",
                "personal_tags", "anime_personal_tags", "screenshots", "screenshot_personal_tags",
                "manual_watch_events", "tracking_events", "recommendation_feature_preferences",
                "recommendation_hidden_anime", "external_change_receipts",
            ];
            Assert.All(expectedTables, table => Assert.Contains(table, tables));
            var before = await SnapshotTablesAsync(connection, tables);

            await RunProductionMigrationAsync();

            Assert.Equal(before, await SnapshotTablesAsync(connection, tables));
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }

        private static async Task<string> SnapshotTablesAsync(
            Microsoft.Data.Sqlite.SqliteConnection connection, List<string> tables)
        {
            var snapshot = new Dictionary<string, List<object?[]>>();
            foreach (var table in tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
                using var reader = await command.ExecuteReaderAsync();
                var rows = new List<object?[]>();
                while (await reader.ReadAsync())
                    rows.Add(Enumerable.Range(0, reader.FieldCount)
                        .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray());
                snapshot[table] = rows;
            }
            return System.Text.Json.JsonSerializer.Serialize(snapshot);
        }

        [Fact]
        public async Task Migration_FromV7ToV8_RestoresOnlyLegacyStartedCatchUpPlans()
        {
            await RunProductionMigrationAsync();

            await using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt) VALUES
                        (801, 2, '2026-09-01T00:00:00Z'),
                        (802, 5, '2026-09-01T00:00:00Z'),
                        (803, 2, '2026-09-01T00:00:00Z');
                    INSERT INTO anime_plans(
                        AnimeId, TitleSnapshot, Priority, CreatedAt,
                        UpdatedAt, StartedAt, ArchivedAt) VALUES
                        (801, '旧版已开始', 1, '2026-09-01T00:00:00Z',
                         '2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z',
                         '2026-09-02T00:00:00Z'),
                        (802, '已经看完', 1, '2026-09-01T00:00:00Z',
                         '2026-09-01T00:00:00Z', '2026-09-02T00:00:00Z',
                         '2026-09-02T00:00:00Z'),
                        (803, '其他归档', 1, '2026-09-01T00:00:00Z',
                         '2026-09-01T00:00:00Z', NULL,
                         '2026-09-02T00:00:00Z');
                    PRAGMA user_version = 7;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            await RunProductionMigrationAsync();

            var plans = new ActionCenterService(DbFactory);
            Assert.Equal(801, Assert.Single(await plans.GetPlansAsync()).AnimeId);
            var restored = await plans.GetPlanAsync(801);
            Assert.NotNull(restored?.StartedAt);
            Assert.Null(restored.ArchivedAt);
            Assert.NotNull((await plans.GetPlanAsync(802))?.ArchivedAt);
            Assert.NotNull((await plans.GetPlanAsync(803))?.ArchivedAt);
        }
    }
}
