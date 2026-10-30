using System.Diagnostics;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace AniMeido.Tests;

[Collection(DevDataRootCollection.Name)]
public sealed class PendingReviewPerformanceTests(ITestOutputHelper output) : DbTestBase
{
    [Theory]
    [InlineData(1_000)]
    [InlineData(10_000)]
    public async Task LocalTargets_PreserveAllIdentities_WithLargeArchiveHistory(int entries)
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                WITH RECURSIVE ids(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<100)
                INSERT INTO anime_archives(AnimeId,TitleSnapshot,SummaryNote,CreatedAt,UpdatedAt)
                SELECT n, printf('Fixture %03d',n), '', '2026-10-22', '2026-10-22' FROM ids;
                WITH RECURSIVE ids(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM ids WHERE n<@entries)
                INSERT INTO archive_entries(EntryId,AnimeId,OccurredAt,Body,CreatedAt,UpdatedAt)
                SELECT printf('entry-%d',n), 1+(n%100), '2026-10-22', @body, '2026-10-22', '2026-10-22' FROM ids;
                """;
            command.Parameters.AddWithValue("@entries", entries);
            command.Parameters.AddWithValue("@body", new string('x', 512));
            await command.ExecuteNonQueryAsync();
        }

        var service = new PendingReviewService(DbFactory,
            new PersonalIdRemapService(DbFactory, NullLogger<PersonalIdRemapService>.Instance));
        await service.GetLocalTargetsAsync();
        var times = new List<double>();
        var allocations = new List<long>();
        for (var run = 0; run < 5; run++)
        {
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var started = Stopwatch.GetTimestamp();
            var targets = await service.GetLocalTargetsAsync();
            times.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            allocations.Add(GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore);
            Assert.Equal(100, targets.Count);
            Assert.Equal(Enumerable.Range(1, 100), targets.Select(item => item.AnimeId));
        }
        times.Sort();
        allocations.Sort();
        output.WriteLine($"GetLocalTargets warm; archives=100; entries={entries}; body=512 chars; runs=5; " +
            $"median={times[2]:F2}ms; allocated median={allocations[2]} bytes; max={times[^1]:F2}ms");
    }
}
