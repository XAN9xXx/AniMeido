using AniMeido.Contracts.PersonalAnime;
using System.Diagnostics;

namespace AniMeido.Tests;

public sealed partial class PersonalAnimeGatewayReadTests
{
    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public async Task ContextCost_UnrequestedTrackingAndProgressDoNotChangeTheSnapshot(int count)
    {
        await RunProductionMigrationAsync();
        await using var fixture = new ReadFixture(DbFactory);
        await fixture.Cache.CleanExpiredAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                WITH RECURSIVE ids(id) AS (
                    SELECT 1 UNION ALL SELECT id + 1 FROM ids WHERE id < @count
                )
                INSERT INTO tracking(AnimeID, Status, UpdatedAt)
                SELECT id, 1, @at FROM ids;

                INSERT INTO anime_progress(
                    AnimeId, CurrentEpisode, PositionSeconds, DurationSeconds, LastWatchedAt)
                SELECT AnimeID, 3, 420, 1200, @at FROM tracking;
                """;
            command.Parameters.AddWithValue("@count", count);
            command.Parameters.AddWithValue("@at", At.ToString("O"));
            await command.ExecuteNonQueryAsync();
            transaction.Commit();
        }

        await GuardAgainstWritesAsync();
        var before = await SnapshotAsync();
        var request = new PersonalAnimeContextRequest("scope cost", [17], PersonalAnimeDataCategory.None);
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await fixture.Gateway.BuildContextAsync(request);
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            var result = await fixture.Gateway.BuildContextAsync(request);
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocated));

            var item = Assert.Single(result.Items);
            Assert.Equal(17, item.AnimeId);
            Assert.Equal("未命名作品", item.Title);
            Assert.Null(item.TrackingStatus);
            Assert.Null(item.Progress);
            Assert.Null(item.Plan);
        }

        output.WriteLine("{0} tracking and progress rows: median {1:F3} ms, {2:N0} B",
            count,
            samples.Select(sample => sample.Milliseconds).Order().ElementAt(2),
            samples.Select(sample => sample.Bytes).Order().ElementAt(2));
        Assert.Equal(0, fixture.Source.Calls);
        Assert.Equal(before, await SnapshotAsync());
    }
}
