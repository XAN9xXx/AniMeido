using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class PublicIdCheckPerformanceTests(ITestOutputHelper output) : DbTestBase
{
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task SingleWorkCheckWithLargeHistoryKeepsRequestsAndRecords(int entries)
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE ids(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<100)
                INSERT INTO anime_archives(AnimeId,TitleSnapshot,SummaryNote,CreatedAt,UpdatedAt)
                SELECT n, printf('Fixture %03d',n), '', '2026-10-26', '2026-10-26' FROM ids;
                WITH RECURSIVE ids(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<@entries)
                INSERT INTO archive_entries(EntryId,AnimeId,OccurredAt,Body,CreatedAt,UpdatedAt)
                SELECT printf('entry-%d',n), 1+(n%100), '2026-10-26', @body, '2026-10-26', '2026-10-26' FROM ids;
                """;
            command.Parameters.AddWithValue("@entries", entries);
            command.Parameters.AddWithValue("@body", new string('x', 512));
            await command.ExecuteNonQueryAsync();
        }

        using var handler = new PendingReviewTests.CheckHandler();
        using var http = handler.Client();
        var metadata = new MetadataDataSource(NullLogger<MetadataDataSource>.Instance, http, new CacheService(DbFactory));
        var remap = new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance);
        await using var checker = new PublicIdCheckService(DbFactory, http, metadata, remap,
            NullLogger<PublicIdCheckService>.Instance);
        await checker.CheckAsync("v1", 1);
        var elapsed = new List<double>();
        var allocated = new List<long>();
        for (var run = 0; run < 5; run++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            await checker.CheckAsync("v1", 1);
            elapsed.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocated.Add(GC.GetTotalAllocatedBytes(precise: true) - before);
        }

        Assert.Equal(6, handler.Paths.Count);
        Assert.All(handler.Paths, path => Assert.Equal(new[] { 1 }, PendingReviewTests.CheckHandler.Ids(path)));
        using var verify = await DbFactory.OpenAsync();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM archive_entries";
        Assert.Equal((long)entries, await count.ExecuteScalarAsync());
        output.WriteLine("Single-work check; archives=100; entries={0}; body=512 chars; warm; " +
            "5-run median={1:F3} ms / {2:N0} B", entries, elapsed.Order().ElementAt(2), allocated.Order().ElementAt(2));
    }
}
