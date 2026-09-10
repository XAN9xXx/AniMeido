using Microsoft.Data.Sqlite;

namespace AniMeido.Tests
{
    /// <summary>
    /// 直接通过 SQLite 测试 cache 表的读写行为。
    /// 不依赖 CacheService（internal 类），仅验证表结构和 SQL。
    /// </summary>
    public class CacheServiceTests : DbTestBase
    {
        [Fact]
        public async Task CacheTable_InsertAndSelect_ReturnsData()
        {
            await CreateBaseTablesAsync();

            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cache (CacheKey, Data, ExpiresAt) VALUES (@k, @d, @e)";
            cmd.Parameters.AddWithValue("@k", "key1");
            cmd.Parameters.AddWithValue("@d", "hello");
            cmd.Parameters.AddWithValue("@e", DateTime.UtcNow.AddHours(1).ToString("O"));
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT Data FROM cache WHERE CacheKey = @k AND ExpiresAt > @now";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            var result = await cmd.ExecuteScalarAsync();

            Assert.Equal("hello", result);
        }

        [Fact]
        public async Task CacheTable_ExpiredEntry_NotReturned()
        {
            await CreateBaseTablesAsync();

            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cache (CacheKey, Data, ExpiresAt) VALUES (@k, @d, @e)";
            cmd.Parameters.AddWithValue("@k", "key1");
            cmd.Parameters.AddWithValue("@d", "stale");
            cmd.Parameters.AddWithValue("@e", DateTime.UtcNow.AddDays(-1).ToString("O"));
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT Data FROM cache WHERE CacheKey = @k AND ExpiresAt > @now";
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            var result = await cmd.ExecuteScalarAsync();

            Assert.Null(result);
        }

        [Fact]
        public async Task CacheTable_AllowExpired_ReturnsStale()
        {
            await CreateBaseTablesAsync();

            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cache (CacheKey, Data, ExpiresAt) VALUES (@k, @d, @e)";
            cmd.Parameters.AddWithValue("@k", "key1");
            cmd.Parameters.AddWithValue("@d", "stale");
            cmd.Parameters.AddWithValue("@e", DateTime.UtcNow.AddDays(-1).ToString("O"));
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT Data FROM cache WHERE CacheKey = @k ORDER BY ExpiresAt DESC LIMIT 1";
            var result = await cmd.ExecuteScalarAsync();

            Assert.Equal("stale", result);
        }

        [Fact]
        public async Task CacheTable_DeleteAll_ClearsTable()
        {
            await CreateBaseTablesAsync();

            using var conn = new SqliteConnection(ConnectionString);
            await conn.OpenAsync();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO cache (CacheKey, Data, ExpiresAt) VALUES ('k1', 'd1', '3000-01-01')";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "DELETE FROM cache";
            await cmd.ExecuteNonQueryAsync();

            cmd.CommandText = "SELECT COUNT(*) FROM cache";
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            Assert.Equal(0, count);
        }
    }
}
