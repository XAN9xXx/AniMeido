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
            Assert.Equal(4, version);

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
            Assert.Equal(4, Convert.ToInt32(await versionCmd.ExecuteScalarAsync()));

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
            Assert.Equal(4, version);
        }
    }
}
