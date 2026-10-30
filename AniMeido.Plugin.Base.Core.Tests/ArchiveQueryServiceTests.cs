using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class ArchiveQueryServiceTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory DbFactory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task PanelQueries_ReturnCompleteDataAndRespectOptionalPlayback()
    {
        var archive = new ArchiveService(DbFactory);
        await archive.UpsertArchiveAsync(42, "作品", 8, "感想");
        using (var connection = await DbFactory.OpenAsync())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE anime_archives
                SET CreatedAt = '2025-06-01T00:00:00.0000000+00:00',
                    UpdatedAt = '2025-06-01T00:00:00.0000000+00:00'
                WHERE AnimeId = 42
                """;
            await command.ExecuteNonQueryAsync();
        }
        var queries = new ArchiveQueryService(archive);

        Assert.Equal(42, Assert.Single(await queries.LoadArchivesAsync()).Archive.AnimeId);
        var review = await queries.LoadReviewAsync(2025);
        Assert.Equal(42, Assert.Single(review.Covers).AnimeId);
        Assert.Equal(1, review.Statistics.ArchiveCount);
        var detail = await queries.LoadDetailAsync(42, includePlayback: false);
        Assert.Empty(detail.Timeline.WatchHistory);
        Assert.Empty(detail.Screenshots);
        var gallery = await queries.LoadScreenshotsAsync();
        Assert.Empty(gallery.Screenshots);
        Assert.Empty(gallery.Tags);
    }
}
