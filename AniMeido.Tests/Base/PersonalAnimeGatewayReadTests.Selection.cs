using AniMeido.Contracts.Models;
using AniMeido.Contracts.PersonalAnime;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed partial class PersonalAnimeGatewayReadTests
{
    [Fact]
    public async Task SelectionReader_PreservesTitleTimestampAndStableSourcePriority()
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO tracking(AnimeId, Status, UpdatedAt)
                VALUES(1, 1, 'invalid'), (2, 1, @at);
                INSERT INTO anime_plans(AnimeId, TitleSnapshot, CreatedAt, UpdatedAt, ArchivedAt)
                VALUES(1, 'Plan One', @at, '2024-01-01T00:00:00+00:00', NULL),
                      (3, 'Plan Three', @at, @at, NULL),
                      (6, 'Archived Plan', @at, @at, @at);
                INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, CreatedAt, UpdatedAt)
                VALUES(1, 'Archive One', 8.5, @at, '2023-01-01T00:00:00+00:00'),
                      (-7, 'Local Archive', NULL, @at, @at),
                      (4, 'Archive Four', NULL, @at, @at);
                INSERT INTO browse_history(AnimeId, TitleSnapshot, LastViewedAt, ViewCount)
                VALUES(1, 'Browse One', @at, 1), (5, 'Browse Five', @at, 1);
                """;
            seed.Parameters.AddWithValue("@at", At.ToString("O"));
            await seed.ExecuteNonQueryAsync();
        }

        var result = await PersonalAnimeSelectionReader.ReadAsync(DbFactory, new(), default);

        Assert.Equal(new[] { 2, 3, -7, 4, 5, 1 }, result.Select(item => item.AnimeId));
        Assert.Equal(new[] { "未命名作品", "Plan Three", "Local Archive", "Archive Four", "Browse Five", "Archive One" },
            result.Select(item => item.Title));
        var combined = result[^1];
        Assert.Equal(AnimeTrackingStatus.Watching, combined.TrackingStatus);
        Assert.True(combined.HasArchive);
        Assert.True(combined.HasPlan);
        Assert.Equal(8.5, combined.PersonalRating);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), combined.UpdatedAt);
        Assert.Equal(At, result.Single(item => item.AnimeId == 5).UpdatedAt);

        var filtered = await PersonalAnimeSelectionReader.ReadAsync(DbFactory,
            new(SearchText: " archive ", ArchivesOnly: true, PlansOnly: true,
                TrackingStatuses: [AnimeTrackingStatus.Watching]), default);
        Assert.Equal(combined, Assert.Single(filtered));
    }

    [Fact]
    public async Task SelectionReader_UsesOnlyTheExistingRecentHistoryWindow()
    {
        await RunProductionMigrationAsync();
        using (var connection = await DbFactory.OpenAsync())
        using (var transaction = connection.BeginTransaction())
        using (var seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = """
                INSERT INTO browse_history(AnimeId, TitleSnapshot, LastViewedAt, ViewCount)
                VALUES(@id, 'History Title', @at, 1)
                """;
            var id = seed.Parameters.AddWithValue("@id", 0);
            var at = seed.Parameters.AddWithValue("@at", "");
            for (var index = 1; index <= 205; index++)
            {
                id.Value = index;
                at.Value = At.AddMinutes(index).ToString("O");
                await seed.ExecuteNonQueryAsync();
            }

            seed.CommandText = "INSERT INTO tracking(AnimeId, Status, UpdatedAt) VALUES(1, 1, @at)";
            at.Value = At.ToString("O");
            await seed.ExecuteNonQueryAsync();
            transaction.Commit();
        }

        var all = await PersonalAnimeSelectionReader.ReadAsync(DbFactory, new(Limit: 500), default);
        Assert.Equal(Enumerable.Range(6, 200).Reverse(), all.Select(item => item.AnimeId));
        var tracked = await PersonalAnimeSelectionReader.ReadAsync(DbFactory,
            new(TrackingStatuses: [AnimeTrackingStatus.Watching]), default);
        Assert.Equal("未命名作品", Assert.Single(tracked).Title);
    }

    [Fact]
    public async Task SelectionReader_PreCancelledReadDoesNotOpenTheDatabase()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PersonalAnimeSelectionReader.ReadAsync(DbFactory, new(), cancellation.Token));
        Assert.False(File.Exists(DbPath));
    }
}
