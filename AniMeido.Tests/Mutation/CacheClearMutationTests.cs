using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class CacheClearMutationTests : DbTestBase
{
    [Fact]
    public async Task ClearAll_SqlFailureStillInvalidatesValidAndExpiredHotEntries()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.CleanExpiredAsync();
        await cache.SetCacheAsync("valid-hot", "fresh", TimeSpan.FromDays(1));
        await cache.SetCacheAsync("expired-hot", "offline", TimeSpan.FromDays(-2));
        Assert.Equal("fresh", await cache.GetCacheAsync("valid-hot"));
        Assert.Equal("offline", await cache.GetCacheAllowExpiredAsync("expired-hot"));

        // 成功清空会调用固定指向真实 AppData 的图片缓存门面。以临时库触发器让 SQL 清空失败，
        // 在进入图片清理前结束；随后仅从 SQL 删除数据，观察服务是否仍错误地返回原内存条目。
        await ExecuteAsync("""
            CREATE TRIGGER fail_clear BEFORE DELETE ON cache
            BEGIN SELECT RAISE(ABORT, 'fixture clear failure'); END;
            """);
        var error = await Assert.ThrowsAsync<SqliteException>(() => cache.ClearAllCacheAsync());
        Assert.Equal(19, error.SqliteErrorCode);
        Assert.Equal(2, await RowCountAsync());

        await ExecuteAsync("DROP TRIGGER fail_clear; DELETE FROM cache;");
        Assert.Equal(0, await RowCountAsync());
        Assert.Null(await cache.GetCacheAsync("valid-hot"));
        Assert.Null(await cache.GetCacheAllowExpiredAsync("valid-hot"));
        Assert.Null(await cache.GetCacheAllowExpiredAsync("expired-hot"));
    }

    [Fact]
    public async Task ClearAll_RejectsWritebackFromRequestStartedBeforeClear()
    {
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory);
        await cache.CleanExpiredAsync();
        // 模拟清空前已发出、清空后才返回的请求：先记下当时的代次。
        var staleGeneration = cache.CaptureGeneration();
        // 行级触发器只在有行被删除时触发；表为空时清空会成功并删除真实 AppData 图片缓存。
        await cache.SetCacheAsync("existing", "old", TimeSpan.FromDays(1));
        Assert.Equal(1, await RowCountAsync());

        // 同上，用失败的 SQL 清空避开真实 AppData 图片缓存；代次在清 SQL 之前已经递增。
        await ExecuteAsync("""
            CREATE TRIGGER fail_clear BEFORE DELETE ON cache
            BEGIN SELECT RAISE(ABORT, 'fixture clear failure'); END;
            """);
        await Assert.ThrowsAsync<SqliteException>(() => cache.ClearAllCacheAsync());
        await ExecuteAsync("DROP TRIGGER fail_clear;");

        await cache.SetCacheAsync("late", "stale", TimeSpan.FromDays(1), staleGeneration);
        Assert.Equal(1, await RowCountAsync());
        Assert.Null(await cache.GetCacheAllowExpiredAsync("late"));

        // 清空之后发出的请求照常写入。
        await cache.SetCacheAsync("fresh", "new", TimeSpan.FromDays(1), cache.CaptureGeneration());
        Assert.Equal("new", await cache.GetCacheAsync("fresh"));
        Assert.Equal(2, await RowCountAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> RowCountAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cache";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
