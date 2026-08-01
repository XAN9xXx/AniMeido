using AniMeido.App.Services;

namespace AniMeido.Tests
{
    public class DatabaseMigrationTests : DbTestBase
    {
        /// <summary>使用生产 DatabaseService 执行完整迁移。</summary>
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

            // 检查 user_version
            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(6, version);

            // 检查所有表是否存在
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

            // 执行 migration（使用生产 DatabaseService）
            await RunProductionMigrationAsync();

            // 验证 v1 数据还在
            using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verifyConn.OpenAsync();
            var check = verifyConn.CreateCommand();
            check.CommandText = "SELECT Status FROM tracking WHERE AnimeID = 1";
            var status = Convert.ToInt32(await check.ExecuteScalarAsync());
            Assert.Equal(1, status);

            // 验证 v2 新增表存在
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

            // 执行迁移
            await RunProductionMigrationAsync();

            // 验证 user_version
            using var verifyConn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await verifyConn.OpenAsync();
            var versionCmd = verifyConn.CreateCommand();
            versionCmd.CommandText = "PRAGMA user_version";
            Assert.Equal(6, Convert.ToInt32(await versionCmd.ExecuteScalarAsync()));

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
            // 第二次运行
            await RunProductionMigrationAsync();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(6, version);
        }

        [Fact]
        public async Task Migration_FromV4ToV5_BacksUpAndPreservesTracking()
        {
            using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var setupCommand = connection.CreateCommand();
                setupCommand.CommandText = """
                    CREATE TABLE tracking(
                        AnimeID INTEGER PRIMARY KEY,
                        Status INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL);
                    CREATE TABLE cache(
                        CacheKey TEXT PRIMARY KEY,
                        Data TEXT NOT NULL,
                        ExpiresAt TEXT NOT NULL);
                    CREATE TABLE config(
                        Key TEXT PRIMARY KEY,
                        Value TEXT NOT NULL);
                    CREATE TABLE saved_tags(
                        TagName TEXT NOT NULL PRIMARY KEY);
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(88, 5, '2026-07-01T00:00:00Z');
                    PRAGMA user_version = 4;
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
            Assert.NotEmpty(Directory.GetFiles(
                Paths.BackupDirectory,
                "AniMeido-*.db"));
        }

        [Fact]
        public async Task Migration_FromV5ToV6_PreservesTrackingAndCreatesRecommendationTables()
        {
            await using (var connection =
                new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE tracking(
                        AnimeID INTEGER PRIMARY KEY,
                        Status INTEGER NOT NULL,
                        UpdatedAt TEXT NOT NULL);
                    INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                    VALUES(96, 1, '2026-08-01T00:00:00Z');
                    PRAGMA user_version = 5;
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
            Assert.NotEmpty(Directory.GetFiles(
                Paths.BackupDirectory,
                "AniMeido-*.db"));
        }
    }
}
