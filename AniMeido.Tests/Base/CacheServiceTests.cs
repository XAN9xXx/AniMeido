using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests
{
    /// <summary>
    /// 保留直接验证 cache 表结构和 SQL 的测试，并覆盖 CacheService 的保留期与离线兜底。
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

        [Theory]
        [InlineData(-24, true, false)]
        [InlineData(-1, true, false)]
        [InlineData(-720, false, false)]
        [InlineData(-744, false, false)]
        [InlineData(24, true, true)]
        public async Task CleanExpired_RetainsFor30DaysAndKeepsValiditySeparate(
            int expiresInHours, bool retained, bool valid)
        {
            await CreateBaseTablesAsync();
            const string key = "retention-test";
            const string data = "retained-data";
            await SeedCacheAsync(key, data, DateTime.UtcNow.AddHours(expiresInHours));

            var cache = new CacheService(DbFactory);
            await cache.CleanExpiredAsync();

            Assert.Equal(retained ? data : null, await ReadCacheRowAsync(key));
            Assert.Equal(retained ? data : null, await cache.GetCacheAllowExpiredAsync(key));
            // 先离线读取回填内存，再验证正常读取；UTC 时间不能被本地时区误判为有效。
            Assert.Equal(valid ? data : null, await cache.GetCacheAsync(key));
            Assert.Equal(retained ? data : null, await ReadCacheRowAsync(key));
        }

        [Theory]
        [InlineData(-24, true)]
        [InlineData(-744, false)]
        public async Task CleanExpired_AppliesRetentionToMemoryCache(int expiresInHours, bool retained)
        {
            await CreateBaseTablesAsync();
            var cache = new CacheService(DbFactory);
            await cache.CleanExpiredAsync();
            const string key = "memory-retention";
            const string data = "memory-data";
            await cache.SetCacheAsync(key, data, TimeSpan.FromHours(expiresInHours));

            await cache.CleanExpiredAsync();

            Assert.Equal(retained ? data : null, await ReadCacheRowAsync(key));
            // 直接移除 SQLite 副本以隔离内存层，避免错误淘汰内存后从数据库回填而掩盖问题。
            using (var connection = await DbFactory.OpenAsync())
            {
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM cache WHERE CacheKey = @key";
                command.Parameters.AddWithValue("@key", key);
                await command.ExecuteNonQueryAsync();
            }
            Assert.Null(await cache.GetCacheAsync(key));
            Assert.Equal(retained ? data : null, await cache.GetCacheAllowExpiredAsync(key));
        }

        [Fact]
        public async Task Restart_OfflineSeasonRequestReturnsRetainedExpiredCache()
        {
            await CreateBaseTablesAsync();
            const string version = "20261019.2";
            var key = MetadataDataSource.GetSeasonCacheKey(2025, Season.Spring, version);
            var data = """
                {"dataVersion":"20261019.2","total":1,"items":[
                    {"id":321,"names":[{"kind":"main","lang":"zh-Hans","text":"离线季度缓存"}],"firstDate":"2025-04-01"}
                ]}
                """;
            await SeedCacheAsync(key, data, DateTime.UtcNow.AddDays(-2));
            await SeedCacheAsync(MetadataDataSource.VersionCacheKey, version, DateTime.UtcNow.AddDays(1));

            // 数据已经在 SQLite；新实例的内存缓存为空，模拟应用重启。
            var cache = new CacheService(DbFactory);
            await cache.CleanExpiredAsync();
            Assert.Null(await cache.GetCacheAsync(key));
            var handler = new OfflineHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://metadata.example.test") };
            var source = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, client, cache);

            var result = await source.GetAnimeBySeasonAsync(2025, Season.Spring, CancellationToken.None);

            var restored = Assert.Single(result);
            Assert.Equal(321, restored.ID);
            Assert.Equal("离线季度缓存", restored.Title);
            Assert.Equal(2, handler.RequestCount); // HEAD 版本检查、季度 GET；均由假 handler 拦截。
            Assert.Equal(data, await ReadCacheRowAsync(key));
            Assert.Null(await cache.GetCacheAsync(key));
        }

        private async Task SeedCacheAsync(string key, string data, DateTime expiresAt)
        {
            using var connection = await DbFactory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO cache(CacheKey, Data, ExpiresAt) VALUES (@key, @data, @expiresAt)";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@data", data);
            command.Parameters.AddWithValue("@expiresAt", expiresAt.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        private async Task<string?> ReadCacheRowAsync(string key)
        {
            using var connection = await DbFactory.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Data FROM cache WHERE CacheKey = @key";
            command.Parameters.AddWithValue("@key", key);
            return (string?)await command.ExecuteScalarAsync();
        }

        private sealed class OfflineHandler : HttpMessageHandler
        {
            public int RequestCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                RequestCount++;
                return Task.FromException<HttpResponseMessage>(new HttpRequestException("Offline test"));
            }
        }
    }
}
