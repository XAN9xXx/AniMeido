namespace AniMeido.Tests
{
    public class DatabaseMigrationTests : DbTestBase
    {
        [Fact]
        public async Task FullMigration_CreatesAllTables()
        {
            await RunFullMigrationAsync();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await conn.OpenAsync();

            // 检查 user_version
            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(2, version);

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

            // 执行 migration（从 v1 到 v2）
            await RunFullMigrationAsync();

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
        public async Task Migration_Idempotent_RunningTwiceIsSafe()
        {
            await RunFullMigrationAsync();
            // 第二次运行
            await RunFullMigrationAsync();

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(2, version);
        }
    }
}
