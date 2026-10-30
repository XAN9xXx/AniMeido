using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using System.Globalization;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class CacheExpiryIndexEvaluationTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int WriteBatchSize = 32;
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory Factory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1000, 1)]
    [InlineData(1000, 50)]
    [InlineData(10000, 0)]
    [InlineData(10000, 1)]
    [InlineData(10000, 50)]
    [Trait("Category", "Performance")]
    public async Task TemporaryExpiryIndex_PreservesRetentionAndMeasuresCleanupWriteTradeoff(int count, int expiredPercent)
    {
        var now = DateTime.UtcNow;
        var expiredCount = count * expiredPercent / 100;
        var rows = Enumerable.Range(0, count).Select(index => new CacheRow(
            $"fixture-{index:D5}",
            $"payload-{index:D5}:" + new string('x', 512),
            now.AddDays(index < expiredCount ? -31 : index % 2 == 0 ? -29 : 1))).ToArray();
        var retained = rows.First(row => row.ExpiresAt < now && row.ExpiresAt > now.AddDays(-30));
        var valid = rows.First(row => row.ExpiresAt > now);
        var cutoff = now.AddDays(-30).ToString("O");

        await ResetRowsAsync(rows);
        var beforePlan = await ReadDeletePlanAsync(cutoff);
        var before = await MeasureScenarioAsync(rows, retained, valid, expiredCount, "Before");

        await ResetRowsAsync(rows);
        var beforePages = await ReadPagesAsync();
        var build = Stopwatch.StartNew();
        using (var connection = await Factory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 候选仅存在于夹具临时库；不修改产品 schema。
            command.CommandText = "CREATE INDEX candidate_cache_expiry ON cache(ExpiresAt)";
            await command.ExecuteNonQueryAsync();
        }
        build.Stop();
        var afterPages = await ReadPagesAsync();
        var afterPlan = await ReadDeletePlanAsync(cutoff);
        var after = await MeasureScenarioAsync(rows, retained, valid, expiredCount, "After");

        output.WriteLine("Rows={0}; BeyondThirtyDayRetention={1} ({2}%); PayloadSuffix=512 characters; warm database",
            count, expiredCount, expiredPercent);
        output.WriteLine("3 warmups / 5 samples; full fixture data rebuilt outside EVERY destructive timed sample.");
        output.WriteLine("Index build (one sample): {0:F3} ms; database page bytes: {1} -> {2}; active page bytes: {3} -> {4}",
            build.Elapsed.TotalMilliseconds, beforePages.TotalBytes, afterPages.TotalBytes,
            beforePages.ActiveBytes, afterPages.ActiveBytes);
        output.WriteLine("Active page increment excludes reusable freelist pages: {0} B", afterPages.ActiveBytes - beforePages.ActiveBytes);
        output.WriteLine("Before DELETE plan: {0}", beforePlan);
        output.WriteLine("After DELETE plan: {0}", afterPlan);
        Assert.Contains("candidate_cache_expiry", afterPlan);
        ReportComparison("Production cleanup", before.Cleanup, after.Cleanup);
        ReportComparison($"Production SetCacheAsync: {WriteBatchSize} new keys", before.Insert, after.Insert);
        ReportComparison($"Production SetCacheAsync: {WriteBatchSize} overwrites", before.Overwrite, after.Overwrite);
        output.WriteLine("Process allocations are comparable only when this class runs alone; timings do not represent UI acceptance.");
    }

    private async Task<Scenario> MeasureScenarioAsync(
        IReadOnlyList<CacheRow> rows, CacheRow retained, CacheRow valid, int expiredCount, string label)
    {
        CacheService cache = null!;
        async Task PrepareAsync()
        {
            await ResetRowsAsync(rows);
            cache = new CacheService(Factory);
            // 两种保留内容也进入内存，避免只核对 SQLite 而漏掉生产清理的内存语义。
            Assert.Equal(retained.Data, await cache.GetCacheAllowExpiredAsync(retained.Key));
            Assert.Equal(valid.Data, await cache.GetCacheAsync(valid.Key));
        }

        var cleanup = await MeasureAsync(
            PrepareAsync,
            () => cache.CleanExpiredAsync(),
            async () =>
            {
                await AssertRowsAsync(rows.Where(row => row.ExpiresAt > DateTime.UtcNow.AddDays(-30)).ToArray());
                Assert.Equal(rows.Count - expiredCount, await CountAsync());
                Assert.Null(await cache.GetCacheAsync(retained.Key));
                Assert.Equal(retained.Data, await cache.GetCacheAllowExpiredAsync(retained.Key));
                Assert.Equal(valid.Data, await cache.GetCacheAsync(valid.Key));
                var coldReader = new CacheService(Factory);
                Assert.Equal(retained.Data, await coldReader.GetCacheAllowExpiredAsync(retained.Key));
                Assert.Equal(valid.Data, await coldReader.GetCacheAsync(valid.Key));
                if (expiredCount > 0)
                {
                    Assert.Null(await coldReader.GetCacheAllowExpiredAsync(rows[0].Key));
                }
            });
        var insert = await MeasureAsync(
            PrepareAsync,
            () => WriteBatchAsync(cache, overwrite: false),
            () => AssertWriteBatchAsync(cache, rows.Count, overwrite: false));
        var overwrite = await MeasureAsync(
            async () =>
            {
                await PrepareAsync();
                await WriteBatchAsync(cache, overwrite: false);
            },
            () => WriteBatchAsync(cache, overwrite: true),
            () => AssertWriteBatchAsync(cache, rows.Count, overwrite: true));
        output.WriteLine("{0}: cleanup retained {1} rows; all retained key/data/expiry fields and hot/cold fallback checked.",
            label, rows.Count - expiredCount);
        return new Scenario(cleanup, insert, overwrite);
    }

    private async Task ResetRowsAsync(IReadOnlyList<CacheRow> rows)
    {
        using var connection = await Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM cache";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "INSERT INTO cache(CacheKey, Data, ExpiresAt) VALUES(@key, @data, @expiresAt)";
        var key = command.Parameters.AddWithValue("@key", "");
        var data = command.Parameters.AddWithValue("@data", "");
        var expiry = command.Parameters.AddWithValue("@expiresAt", "");
        foreach (var row in rows)
        {
            key.Value = row.Key;
            data.Value = row.Data;
            expiry.Value = row.ExpiresAt.ToString("O");
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
    }

    private async Task AssertRowsAsync(IReadOnlyList<CacheRow> expected)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CacheKey, Data, ExpiresAt FROM cache ORDER BY CacheKey";
        using var reader = await command.ExecuteReaderAsync();
        var index = 0;
        while (await reader.ReadAsync())
        {
            Assert.True(index < expected.Count);
            var row = expected[index++];
            Assert.Equal(row.Key, reader.GetString(0));
            Assert.Equal(row.Data, reader.GetString(1));
            Assert.Equal(row.ExpiresAt.ToString("O"), reader.GetString(2));
        }
        Assert.Equal(expected.Count, index);
    }

    private static async Task WriteBatchAsync(CacheService cache, bool overwrite)
    {
        for (var index = 0; index < WriteBatchSize; index++)
        {
            await cache.SetCacheAsync($"write-{index:D3}", WritePayload(index, overwrite),
                TimeSpan.FromDays(overwrite ? 2 : 1));
        }
    }

    private async Task AssertWriteBatchAsync(CacheService cache, int originalCount, bool overwrite)
    {
        Assert.Equal(originalCount + WriteBatchSize, await CountAsync());
        var coldReader = new CacheService(Factory);
        for (var index = 0; index < WriteBatchSize; index++)
        {
            var key = $"write-{index:D3}";
            var expected = WritePayload(index, overwrite);
            Assert.Equal(expected, await cache.GetCacheAsync(key));
            Assert.Equal(expected, await coldReader.GetCacheAsync(key));
        }
    }

    private static string WritePayload(int index, bool overwrite)
        => $"{(overwrite ? "updated" : "new")}-{index:D3}:" + new string('w', 512);

    private async Task<long> CountAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM cache";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<string> ReadDeletePlanAsync(string cutoff)
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN DELETE FROM cache WHERE ExpiresAt <= @retentionCutoff";
        command.Parameters.AddWithValue("@retentionCutoff", cutoff);
        using var reader = await command.ExecuteReaderAsync();
        var steps = new List<string>();
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(3));
        }
        return string.Join("; ", steps);
    }

    private async Task<Pages> ReadPagesAsync()
    {
        using var connection = await Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT page_count * page_size, (page_count - freelist_count) * page_size
            FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size()
            """;
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Pages(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static async Task<Measurement> MeasureAsync(Func<Task> prepare, Func<Task> action, Func<Task> verify)
    {
        var milliseconds = new double[5];
        var allocated = new long[5];
        for (var iteration = 0; iteration < 8; iteration++)
        {
            // 清理会改变数据；每个预热和正式样本都重新填充，准备和断言不进入计时。
            await prepare();
            var bytes = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            await action();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var allocation = GC.GetTotalAllocatedBytes(precise: true) - bytes;
            if (iteration >= 3)
            {
                milliseconds[iteration - 3] = elapsed;
                allocated[iteration - 3] = allocation;
            }
            await verify();
        }
        return new Measurement(milliseconds, allocated);
    }

    private void ReportComparison(string operation, Measurement before, Measurement after)
    {
        output.WriteLine("{0}: median {1:F3} -> {2:F3} ms; {3} -> {4} B", operation,
            before.Milliseconds.Order().ElementAt(2), after.Milliseconds.Order().ElementAt(2),
            before.Allocated.Order().ElementAt(2), after.Allocated.Order().ElementAt(2));
        output.WriteLine("  Before sample ms: {0}; B: {1}",
            string.Join(", ", before.Milliseconds.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", before.Allocated));
        output.WriteLine("  After sample ms: {0}; B: {1}",
            string.Join(", ", after.Milliseconds.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))),
            string.Join(", ", after.Allocated));
    }

    private sealed record CacheRow(string Key, string Data, DateTime ExpiresAt);
    private sealed record Pages(long TotalBytes, long ActiveBytes);
    private sealed record Measurement(double[] Milliseconds, long[] Allocated);
    private sealed record Scenario(Measurement Cleanup, Measurement Insert, Measurement Overwrite);
}
