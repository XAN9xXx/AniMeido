using Microsoft.Data.Sqlite;

namespace AniMeido.Tests
{
    /// <summary>
    /// 测试基类：为每个测试创建独立的内存 SQLite 数据库。
    /// 测试结束后自动清理。
    /// </summary>
    public abstract class DbTestBase : IDisposable
    {
        protected readonly string DbPath;
        protected readonly string ConnectionString;

        protected DbTestBase()
        {
            DbPath = Path.Combine(Path.GetTempPath(), $"AniMeidoTest_{Guid.NewGuid():N}.db");
            ConnectionString = $"Data Source={DbPath}";
        }

        /// <summary>初始化三张基础表（不含 migration）。</summary>
        protected async Task CreateBaseTablesAsync()
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS tracking(
                    AnimeID   INTEGER PRIMARY KEY,
                    Status    INTEGER NOT NULL,
                    UpdatedAt TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS cache(
                    CacheKey  TEXT PRIMARY KEY,
                    Data      TEXT NOT NULL,
                    ExpiresAt TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS config(
                    Key   TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                )
            """;
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>执行完整的 migration（含 v2 saved_tags）。</summary>
        protected async Task RunFullMigrationAsync()
        {
            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();

            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync());

            if (version < 1)
            {
                await CreateBaseTablesAsync();
                cmd.CommandText = "PRAGMA user_version = 1";
                await cmd.ExecuteNonQueryAsync();
                version = 1;
            }

            if (version < 2)
            {
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS saved_tags(
                        AnimeId INTEGER NOT NULL,
                        TagName TEXT NOT NULL,
                        PRIMARY KEY (AnimeId, TagName)
                    )
                """;
                await cmd.ExecuteNonQueryAsync();
                cmd.CommandText = "PRAGMA user_version = 2";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        /// <summary>模拟数据库损坏（写入无效数据）。</summary>
        protected void CorruptDatabase()
        {
            File.WriteAllBytes(DbPath, new byte[] { 0x00, 0x00, 0x00, 0x00 });
        }

        public void Dispose()
        {
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }
}
