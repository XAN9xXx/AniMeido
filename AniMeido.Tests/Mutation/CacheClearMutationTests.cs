using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class CacheClearMutationTests : DbTestBase
{
    [Fact]
    public async Task ClearAll_SqlFailureStillInvalidatesValidAndExpiredHotEntries()
    {
        using var environment = new DevDataRootEnvironment(Paths.RootDirectory);
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory, () => { });
        await cache.CleanExpiredAsync();
        await cache.SetCacheAsync("valid-hot", "fresh", TimeSpan.FromDays(1));
        await cache.SetCacheAsync("expired-hot", "offline", TimeSpan.FromDays(-2));
        Assert.Equal("fresh", await cache.GetCacheAsync("valid-hot"));
        Assert.Equal("offline", await cache.GetCacheAllowExpiredAsync("expired-hot"));

        // 图片路径同样隔离；触发器只注入 SQL 失败，不承担数据隔离责任。
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
        using var environment = new DevDataRootEnvironment(Paths.RootDirectory);
        await CreateBaseTablesAsync();
        var cache = new CacheService(DbFactory, () => { });
        await cache.CleanExpiredAsync();
        // 模拟清空前已发出、清空后才返回的请求：先记下当时的代次。
        var staleGeneration = cache.CaptureGeneration();
        // 行级触发器需要至少一行才能触发。
        await cache.SetCacheAsync("existing", "old", TimeSpan.FromDays(1));
        Assert.Equal(1, await RowCountAsync());

        // SQL 清理失败后，旧请求仍不能回写；代次在清 SQL 之前已经递增。
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

    [Fact]
    public async Task ClearAll_SuccessClearsOwnedImagesAndRejectsOldWriteback()
    {
        using var environment = new DevDataRootEnvironment(Paths.RootDirectory);
        await CreateBaseTablesAsync();
        var imagePath = Path.Combine(Paths.RootDirectory, "fixture-image.png");
        await File.WriteAllTextAsync(imagePath, "isolated fixture");
        var cache = new CacheService(DbFactory, () => File.Delete(imagePath));
        await cache.CleanExpiredAsync();
        await cache.SetCacheAsync("before", "old", TimeSpan.FromDays(1));
        var oldGeneration = cache.CaptureGeneration();

        await cache.ClearAllCacheAsync();
        await cache.SetCacheAsync("late", "old", TimeSpan.FromDays(1), oldGeneration);

        Assert.False(File.Exists(imagePath));
        Assert.Equal(0, await RowCountAsync());
        Assert.Null(await cache.GetCacheAsync("before"));
        Assert.Null(await cache.GetCacheAsync("late"));
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
