using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class BrowseHistoryIndexEvaluationTests(ITestOutputHelper output) : DbTestBase
{
    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public async Task TemporaryTimeIndex_PreservesHistoryAndMeasuresReadWriteTradeoff(int count)
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO browse_history(AnimeId, TitleSnapshot, LastViewedAt, ViewCount)
                VALUES(@id, @title, @at, 1)
                """;
            var id = seed.Parameters.AddWithValue("@id", 0);
            var title = seed.Parameters.AddWithValue("@title", "");
            var at = seed.Parameters.AddWithValue("@at", "");
            for (var index = 1; index <= count; index++)
            {
                id.Value = index;
                title.Value = $"Work {index:D5}";
                at.Value = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddSeconds(index).ToString("O");
                await seed.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        var history = new BrowseHistoryService(DbFactory);
        var expectedIds = Enumerable.Range(count - 49, 50).Reverse().ToArray();
        async Task ReadAsync()
        {
            var rows = await history.GetHistoryAsync();
            Assert.Equal(expectedIds, rows.Select(row => row.AnimeId));
            Assert.All(rows, row => Assert.Equal(1, row.ViewCount));
        }

        var beforeRead = await MeasureAsync(ReadAsync);
        var beforeWrite = await MeasureAsync(() => WriteAndRollbackAsync(count));
        var beforePlan = await ReadPlanAsync();
        var beforeBytes = await DatabaseBytesAsync();
        var build = Stopwatch.StartNew();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            // 只在本夹具的临时库评估候选索引；产品迁移尚未修改。
            command.CommandText = "CREATE INDEX candidate_browse_time ON browse_history(LastViewedAt DESC)";
            await command.ExecuteNonQueryAsync();
        }
        build.Stop();

        var afterRead = await MeasureAsync(ReadAsync);
        var afterWrite = await MeasureAsync(() => WriteAndRollbackAsync(count));
        var afterPlan = await ReadPlanAsync();
        var addedBytes = await DatabaseBytesAsync() - beforeBytes;
        output.WriteLine("{0} history rows, warm medians (3 warmups / 5 samples):", count);
        output.WriteLine("Read: {0:F3} -> {1:F3} ms; {2:N0} -> {3:N0} B",
            beforeRead.Milliseconds, afterRead.Milliseconds, beforeRead.Bytes, afterRead.Bytes);
        output.WriteLine("1000 updates in rolled-back transaction: {0:F3} -> {1:F3} ms",
            beforeWrite.Milliseconds, afterWrite.Milliseconds);
        output.WriteLine("Index build (one sample): {0:F3} ms; added database pages: {1:N0} B",
            build.Elapsed.TotalMilliseconds, addedBytes);
        output.WriteLine("Before plan: {0}", beforePlan);
        output.WriteLine("After plan: {0}", afterPlan);
        Assert.Contains("candidate_browse_time", afterPlan);
        Assert.DoesNotContain("TEMP B-TREE", afterPlan);
        await ReadAsync();
    }

    private async Task WriteAndRollbackAsync(int count)
    {
        using var connection = await DbFactory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE browse_history
            SET LastViewedAt = @at, ViewCount = ViewCount + 1
            WHERE AnimeId = @id
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        command.Parameters.AddWithValue("@at", "2026-01-01T00:00:00.0000000+00:00");
        for (var index = 1; index <= Math.Min(count, 1000); index++)
        {
            id.Value = index;
            await command.ExecuteNonQueryAsync();
        }

        transaction.Rollback();
    }

    private async Task<string> ReadPlanAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            EXPLAIN QUERY PLAN
            SELECT AnimeId, TitleSnapshot, LastViewedAt, ViewCount
            FROM browse_history
            ORDER BY LastViewedAt DESC LIMIT 50
            """;
        using var reader = await command.ExecuteReaderAsync();
        var steps = new List<string>();
        while (await reader.ReadAsync())
        {
            steps.Add(reader.GetString(3));
        }

        return string.Join("; ", steps);
    }

    private async Task<long> DatabaseBytesAsync()
    {
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(double Milliseconds, long Bytes)> MeasureAsync(Func<Task> action)
    {
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await action();
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            await action();
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before));
        }

        return (samples.Select(sample => sample.Milliseconds).Order().ElementAt(2),
            samples.Select(sample => sample.Bytes).Order().ElementAt(2));
    }
}
