using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class ArchiveReviewQueryPerformanceTests(ITestOutputHelper output) : DbTestBase
{
    private const int ReviewYear = 2025;

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public async Task ReviewQuery_KeepsAnnualCoverSelectionWithoutReadingUnneededArchiveBodies(int count)
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
                VALUES(@id, @title, 8, @note, @created, @updated)
                """;
            var id = command.Parameters.AddWithValue("@id", 0);
            var title = command.Parameters.AddWithValue("@title", "");
            command.Parameters.AddWithValue("@note", new string('文', 8192));
            var created = command.Parameters.AddWithValue("@created", "");
            var updated = command.Parameters.AddWithValue("@updated", "");
            for (var index = 1; index <= count; index++)
            {
                id.Value = index;
                title.Value = $"Work {index:D4}";
                created.Value = new DateTimeOffset(index % 2 == 0 ? ReviewYear : ReviewYear - 1,
                    6, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(index).ToString("O");
                updated.Value = new DateTimeOffset(ReviewYear, 8, 1, 0, 0, 0, TimeSpan.Zero)
                    .AddMinutes(count - index).ToString("O");
                await command.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        var query = new ArchiveQueryService(new ArchiveService(DbFactory));
        async Task ReadAsync()
        {
            var review = await query.LoadReviewAsync(ReviewYear);
            Assert.Equal(3, review.Covers.Count);
            Assert.Equal(new[] { count, count - 2, count - 4 }, review.Covers.Select(item => item.AnimeId));
            Assert.Equal(count / 2, review.Statistics.ArchiveCount);
            Assert.Empty(review.Moments);
            Assert.Empty(review.Screenshots);
        }

        for (var warmup = 0; warmup < 3; warmup++)
        {
            await ReadAsync();
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            await ReadAsync();
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds, GC.GetTotalAllocatedBytes(precise: true) - before));
        }

        output.WriteLine("{0} archives, half in review year, 8192-character notes: median {1:F3} ms, {2:N0} B",
            count,
            samples.Select(sample => sample.Milliseconds).Order().ElementAt(2),
            samples.Select(sample => sample.Bytes).Order().ElementAt(2));

        using var checkConnection = await DbFactory.OpenAsync();
        using var check = checkConnection.CreateCommand();
        check.CommandText = "SELECT COUNT(*), SUM(length(SummaryNote)) FROM anime_archives";
        using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(count, reader.GetInt32(0));
        Assert.Equal(count * 8192L, reader.GetInt64(1));
    }
}
