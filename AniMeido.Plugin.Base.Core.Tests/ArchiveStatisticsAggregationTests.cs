using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveStatisticsAggregationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset OldestRecording = new(2001, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly PersonalDatabaseFixture _database = new();

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(2025, 6, 13)]
    [InlineData(2030, 0, 0)]
    [InlineData(null, 9, 23)]
    public async Task Statistics_PreserveCompletedPlaybackFractionalSumsAndManualRanges(
        int? year, int expectedEpisodes, int expectedMinutes)
    {
        await SeedAsync();

        var statistics = await new ArchiveService(_database.Factory).GetStatisticsAsync(year);

        // 两条年度内播放各 40.25 秒：先相加再换算得到 1 分钟，不能逐条取整。
        // 年度外另有 90.5 秒已完成播放；未完成播放只参与全部历史的最早时间。
        Assert.Equal(expectedEpisodes, statistics.CompletedEpisodeCount);
        Assert.Equal(expectedMinutes, statistics.EstimatedWatchMinutes);
        Assert.Equal(OldestRecording, statistics.RecordingStartedAt);
        Assert.Equal(0, statistics.ArchiveCount);
        Assert.Equal(0, statistics.RatedCount);
        Assert.Equal(0, statistics.EntryCount);
        Assert.Equal(0, statistics.ScreenshotCount);
        Assert.Equal(0, statistics.TrackingChangeCount);
        Assert.Empty(statistics.TagCounts);
    }

    private async Task SeedAsync()
    {
        var current = new DateTimeOffset(2025, 6, 1, 12, 0, 0, TimeSpan.Zero).ToString("O");
        var previous = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero).ToString("O");
        using var connection = await _database.Factory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO watch_sessions(EventId, AnimeId, EpisodeNumber, PositionSeconds, DurationSeconds, IsCompleted, ObservedAt)
            VALUES('current-first', 1, 1, 40.25, 40.25, 1, @current),
                  ('current-second', 1, 2, 40.25, 40.25, 1, @current),
                  ('current-unfinished', 1, 3, 10, 7200, 0, @current),
                  ('previous-completed', 2, 1, 90.5, 90.5, 1, @previous),
                  ('oldest-unfinished', 3, 1, 1, 9000, 0, @oldest);
            INSERT INTO manual_watch_events(EventId, AnimeId, TitleSnapshot, OccurredAt, EpisodeFrom, EpisodeTo, DurationMinutes, Note, CreatedAt)
            VALUES('current-range', 1, 'Fixture', @current, 2, 4, NULL, '', @current),
                  ('current-single', 1, 'Fixture', @current, 7, 7, 12, '', @current),
                  ('previous-range', 2, 'Fixture', @previous, 1, 2, 8, '', @previous);
            """;
        command.Parameters.AddWithValue("@current", current);
        command.Parameters.AddWithValue("@previous", previous);
        command.Parameters.AddWithValue("@oldest", OldestRecording.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
