using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveLookupQuerySemanticsTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory DbFactory => _database.Factory;
    private string DbPath => DbFactory.DatabasePath;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    private static readonly DateTimeOffset At = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, new[] { "manual-negative", "manual-zero", "manual-positive", "player-negative", "player-zero", "player-positive" })]
    [InlineData(42, new[] { "manual-positive", "player-positive" })]
    [InlineData(0, new[] { "manual-zero", "player-zero" })]
    [InlineData(-7, new[] { "manual-negative", "player-negative" })]
    [InlineData(404, new string[0])]
    public async Task WatchHistory_OptionalIdentityPreservesBothKindsAndStoredFields(int? animeId, string[] expectedIds)
    {
        await SeedAsync();
        var archive = new ArchiveService(DbFactory);

        var rows = await archive.GetWatchHistoryAsync(animeId);

        Assert.Equal(expectedIds, rows.Select(row => row.EventId));
        if (animeId is null or 42)
        {
            var player = rows.Single(row => row.EventId == "player-positive");
            Assert.Equal("Archive Title", player.Title);
            Assert.Equal(2, player.EstimatedMinutes);
            Assert.False(player.IsManual);
            Assert.Equal(string.Empty, player.Note);
            var manual = rows.Single(row => row.EventId == "manual-positive");
            Assert.Equal("Manual Title", manual.Title);
            Assert.Null(manual.EstimatedMinutes);
            Assert.True(manual.IsManual);
            Assert.Equal("Manual note", manual.Note);
        }
    }

    [Fact]
    public async Task WatchHistory_EqualTimestampKeepsBothKindsWithoutRequiringAnUnspecifiedTieOrder()
    {
        await SeedAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO watch_sessions VALUES('player-tie',42,4,120,1200,0,@at);
                INSERT INTO manual_watch_events VALUES('manual-tie',42,'Manual Title',@at,4,5,40,'same time',@at)
                """;
            command.Parameters.AddWithValue("@at", At.AddSeconds(10).ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var rows = await new ArchiveService(DbFactory).GetWatchHistoryAsync(42);

        Assert.Equal(new[] { "manual-tie", "player-tie" }, rows.Take(2).Select(row => row.EventId).Order(StringComparer.Ordinal));
        Assert.All(rows.Take(2), row => Assert.Equal(At.AddSeconds(10), row.OccurredAt));
        Assert.Equal(4, rows.Count);
    }

    [Theory]
    [InlineData(null, null, new[] { "positive-next", "positive-at-end", "positive-last-tick", "unbound", "negative", "zero", "positive", "positive-at-start", "positive-old" })]
    [InlineData(42, null, new[] { "positive-next", "positive-at-end", "positive-last-tick", "positive", "positive-at-start", "positive-old" })]
    [InlineData(null, 2025, new[] { "positive-last-tick", "unbound", "negative", "zero", "positive", "positive-at-start" })]
    [InlineData(42, 2025, new[] { "positive-last-tick", "positive", "positive-at-start" })]
    [InlineData(0, 2025, new[] { "zero" })]
    [InlineData(-7, 2025, new[] { "negative" })]
    [InlineData(404, 2025, new string[0])]
    public async Task Screenshots_OptionalIdentityAndYearKeepUnboundRowsAndHalfOpenLocalYear(
        int? animeId, int? year, string[] expectedIds)
    {
        await SeedAsync();

        var rows = await new ArchiveService(DbFactory).GetScreenshotsAsync(animeId, year: year);

        Assert.Equal(expectedIds, rows.Select(row => row.ScreenshotId));
        if (animeId is null or 42)
        {
            var selected = rows.Single(row => row.ScreenshotId == "positive");
            Assert.True(selected.FileExists);
            Assert.Equal("stored-positive", selected.Sha256);
            Assert.Equal("context", selected.ContextNote);
            Assert.Equal(640, selected.Width);
            Assert.Equal(360, selected.Height);
        }
        Assert.All(rows.Where(row => row.ScreenshotId != "positive"), row => Assert.False(row.FileExists));
    }

    [Fact]
    public async Task ScreenshotReader_UsesTheSuppliedTransactionWithTheSameFilters()
    {
        await SeedAsync();
        using var connection = await DbFactory.OpenAsync();
        using var transaction = connection.BeginTransaction(deferred: true);

        var rows = await ArchiveService.ReadScreenshotsAsync(connection, transaction, 42, 2025, default);

        Assert.Equal(new[] { "positive-last-tick", "positive", "positive-at-start" }, rows.Select(row => row.ScreenshotId));
        transaction.Rollback();
    }

    [Fact]
    public async Task PreCancelledReads_DoNotPreventALaterNormalRead()
    {
        await SeedAsync();
        var archive = new ArchiveService(DbFactory);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.GetWatchHistoryAsync(42, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.GetScreenshotsAsync(42, cancellation.Token));
        Assert.Equal(2, (await archive.GetWatchHistoryAsync(42)).Count);
        Assert.Equal(3, (await archive.GetScreenshotsAsync(42, year: 2025)).Count);
    }

    private async Task SeedAsync()
    {
        var existing = Path.Combine(Path.GetDirectoryName(DbPath)!, "existing.png");
        await File.WriteAllTextAsync(existing, "synthetic existing screenshot");
        var start = new DateTimeOffset(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local));
        var end = new DateTimeOffset(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local));
        using var connection = await DbFactory.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO anime_archives(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt) VALUES(42,'Archive Title',@at0,@at0);
            INSERT INTO watch_sessions VALUES('player-positive',42,1,120,150.9,0,@at0),
                                             ('player-zero',0,1,120,1200,1,@at1),
                                             ('player-negative',-7,1,120,1200,1,@at2);
            INSERT INTO manual_watch_events VALUES('manual-positive',42,'Manual Title',@at3,1,2,NULL,'Manual note',@at0),
                                                  ('manual-zero',0,'Manual Zero',@at4,1,2,40,'note',@at0),
                                                  ('manual-negative',-7,'Manual Negative',@at5,1,2,40,'note',@at0);
            INSERT INTO screenshots(ScreenshotId,FilePath,Sha256,CapturedAt,WindowTitle,ProcessName,Width,Height,AnimeId,ContextNote)
            VALUES('positive',@existing,'stored-positive',@at0,'Window','Process',640,360,42,'context'),
                  ('zero',@missing,'stored-zero',@at1,'Window','Process',640,360,0,''),
                  ('negative',@missing,'stored-negative',@at2,'Window','Process',640,360,-7,''),
                  ('unbound',@missing,'stored-unbound',@at3,'Window','Process',640,360,NULL,''),
                  ('positive-old',@missing,'stored-old',@old,'Window','Process',640,360,42,''),
                  ('positive-next',@missing,'stored-next',@next,'Window','Process',640,360,42,''),
                  ('positive-at-start',@missing,'stored-start',@start,'Window','Process',640,360,42,''),
                  ('positive-last-tick',@missing,'stored-last',@last,'Window','Process',640,360,42,''),
                  ('positive-at-end',@missing,'stored-end',@end,'Window','Process',640,360,42,'')
            """;
        for (var index = 0; index <= 5; index++)
        {
            command.Parameters.AddWithValue($"@at{index}", At.AddSeconds(index).ToString("O"));
        }
        command.Parameters.AddWithValue("@existing", existing);
        command.Parameters.AddWithValue("@missing", Path.Combine(Path.GetDirectoryName(DbPath)!, "missing.png"));
        command.Parameters.AddWithValue("@old", At.AddYears(-1).ToString("O"));
        command.Parameters.AddWithValue("@next", At.AddYears(1).ToString("O"));
        command.Parameters.AddWithValue("@start", start.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@last", end.AddTicks(-1).ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("@end", end.ToUniversalTime().ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
