using AniMeido.Plugin.Base.Services;
using System.Diagnostics;
using Xunit.Abstractions;

namespace AniMeido.Tests;

public sealed class ArchiveRatingProjectionPerformanceTests(ITestOutputHelper output) : IAsyncLifetime
{
    private const int SummaryLength = 8192;
    private const int EntryLength = 512;
    private const int WarmupRounds = 3;
    private const int MeasurementRounds = 5;
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(1000)]
    [InlineData(10000)]
    [Trait("Category", "Performance")]
    public async Task RatingsOnly_MatchesFullArchiveProjection_AndReportsItsCost(int count)
    {
        var expected = await SeedAsync(count);
        var archive = new ArchiveService(_database.Factory);
        Func<Task<IReadOnlyDictionary<int, double>>> full = () => ReadFullProjectionAsync(archive);
        Func<Task<IReadOnlyDictionary<int, double>>> narrow = () => archive.GetPersonalRatingsAsync();

        for (var round = 0; round < WarmupRounds; round++)
        {
            AssertRatings(expected, await full());
            AssertRatings(expected, await narrow());
        }

        var fullTimes = new double[MeasurementRounds];
        var fullBytes = new long[MeasurementRounds];
        var narrowTimes = new double[MeasurementRounds];
        var narrowBytes = new long[MeasurementRounds];
        for (var round = 0; round < MeasurementRounds; round++)
        {
            // 交替先后次序；断言在计时和分配采样之后，不把测试比较成本计入查询。
            if (round % 2 == 0)
            {
                (fullTimes[round], fullBytes[round]) = await MeasureAsync(full, expected);
                (narrowTimes[round], narrowBytes[round]) = await MeasureAsync(narrow, expected);
            }
            else
            {
                (narrowTimes[round], narrowBytes[round]) = await MeasureAsync(narrow, expected);
                (fullTimes[round], fullBytes[round]) = await MeasureAsync(full, expected);
            }
        }

        output.WriteLine(
            "Archives={0}; Rated={1}; SummaryChars={2}; Entries={0}; EntryChars={3}; TagsPerArchive=3; Warmups={4}; Measurements={5}",
            count, expected.Count, SummaryLength, EntryLength, WarmupRounds, MeasurementRounds);
        output.WriteLine("FullArchiveThenRatings: MedianTotalMs={0:F3}; MedianProcessAllocatedBytes={1}",
            fullTimes.Order().ElementAt(MeasurementRounds / 2), fullBytes.Order().ElementAt(MeasurementRounds / 2));
        output.WriteLine("GetPersonalRatings: MedianTotalMs={0:F3}; MedianProcessAllocatedBytes={1}",
            narrowTimes.Order().ElementAt(MeasurementRounds / 2), narrowBytes.Order().ElementAt(MeasurementRounds / 2));
        output.WriteLine("Warm synthetic database; setup and assertions excluded. Run this class alone: allocations cover the whole test process, not one thread.");
    }

    private async Task<IReadOnlyDictionary<int, double>> SeedAsync(int count)
    {
        var expected = new Dictionary<int, double>();
        using var connection = await _database.Factory.OpenAsync();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO personal_tags(TagId, Name) VALUES(1, '科幻'), (2, '日常'), (3, '已收藏');
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
            VALUES(@id, @title, @rating, @summary, @at, @at);
            INSERT INTO archive_entries(EntryId, AnimeId, OccurredAt, EpisodeNumber, Body, CreatedAt, UpdatedAt)
            VALUES(@entry, @id, @at, 1, @body, @at, @at);
            INSERT INTO anime_personal_tags(AnimeId, TagId) VALUES(@id, 1), (@id, 2), (@id, 3);
            """;
        var id = command.Parameters.AddWithValue("@id", 0);
        var title = command.Parameters.AddWithValue("@title", string.Empty);
        var rating = command.Parameters.AddWithValue("@rating", DBNull.Value);
        var entry = command.Parameters.AddWithValue("@entry", string.Empty);
        command.Parameters.AddWithValue("@summary", new string('文', SummaryLength));
        command.Parameters.AddWithValue("@body", new string('感', EntryLength));
        command.Parameters.AddWithValue("@at", "2026-10-27T12:00:00.0000000+00:00");
        for (var index = 1; index <= count; index++)
        {
            id.Value = index;
            title.Value = $"作品{index:D5}";
            entry.Value = $"entry-{index:D5}";
            if (index % 2 == 0)
            {
                var value = (index % 20 + 1) * 0.5;
                rating.Value = value;
                expected.Add(index, value);
            }
            else
            {
                rating.Value = DBNull.Value;
            }
            await command.ExecuteNonQueryAsync();
        }
        transaction.Commit();
        return expected;
    }

    private static Task<IReadOnlyDictionary<int, double>> ReadFullProjectionAsync(ArchiveService archive)
        => Task.Run<IReadOnlyDictionary<int, double>>(async () =>
            (await archive.GetArchiveListAsync())
                .Where(item => item.Archive.PersonalRating is not null)
                .ToDictionary(item => item.Archive.AnimeId, item => item.Archive.PersonalRating!.Value));

    private static async Task<(double Milliseconds, long Bytes)> MeasureAsync(
        Func<Task<IReadOnlyDictionary<int, double>>> read,
        IReadOnlyDictionary<int, double> expected)
    {
        var beforeBytes = GC.GetTotalAllocatedBytes(precise: true);
        var started = Stopwatch.GetTimestamp();
        var result = await read();
        var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var bytes = GC.GetTotalAllocatedBytes(precise: true) - beforeBytes;
        AssertRatings(expected, result);
        return (milliseconds, bytes);
    }

    private static void AssertRatings(IReadOnlyDictionary<int, double> expected, IReadOnlyDictionary<int, double> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (id, rating) in expected)
        {
            Assert.True(actual.TryGetValue(id, out var value));
            Assert.Equal(rating, value);
        }
    }
}
