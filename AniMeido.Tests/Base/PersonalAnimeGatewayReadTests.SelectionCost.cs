using AniMeido.Contracts.PersonalAnime;
using System.Diagnostics;

namespace AniMeido.Tests;

public sealed partial class PersonalAnimeGatewayReadTests
{
    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [Trait("Category", "Performance")]
    public async Task SelectionCost_ReturnsCompleteSelectionWithoutLoadingArchiveText(int count)
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
                INSERT INTO anime_archives(AnimeId, TitleSnapshot, SummaryNote, CreatedAt, UpdatedAt)
                VALUES(@id, @title, @note, @at, @at)
                """;
            var id = command.Parameters.AddWithValue("@id", 0);
            var title = command.Parameters.AddWithValue("@title", "");
            command.Parameters.AddWithValue("@note", new string('文', 8192));
            command.Parameters.AddWithValue("@at", At.ToString("O"));
            for (var index = 1; index <= count; index++)
            {
                id.Value = index;
                title.Value = $"Work {index:D4}";
                await command.ExecuteNonQueryAsync();
            }

            transaction.Commit();
        }

        var query = new PersonalAnimeSelectionQuery();
        for (var warmup = 0; warmup < 3; warmup++)
        {
            await fixture.Gateway.QuerySelectionAsync(query);
        }

        var samples = new List<(double Milliseconds, long Bytes)>();
        for (var sample = 0; sample < 5; sample++)
        {
            var allocated = GC.GetTotalAllocatedBytes(precise: true);
            var timer = Stopwatch.StartNew();
            var result = await fixture.Gateway.QuerySelectionAsync(query);
            timer.Stop();
            samples.Add((timer.Elapsed.TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocated));
            Assert.Equal(Math.Min(count, 100), result.Count);
            Assert.Equal(Enumerable.Range(1, result.Count), result.Select(item => item.AnimeId));
            Assert.All(result, item => Assert.True(item.HasArchive));
        }

        output.WriteLine("{0} archives, 8192-character notes: median {1:F3} ms, {2:N0} B",
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
