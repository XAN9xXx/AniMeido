using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class RatedArchiveSeedQueryTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RatedSeedsMatchTheFullArchiveProjectionIncludingEqualUpdateTimes()
    {
        var database = new PersonalDatabaseFixture();
        try
        {
            await database.InitializeAsync();
            await using (var connection = await database.Factory.OpenAsync())
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    INSERT INTO anime_archives(AnimeId, TitleSnapshot, PersonalRating, SummaryNote, CreatedAt, UpdatedAt)
                    VALUES
                        (41, '', 10, 'unused summary', @created, @older),
                        (18, '平局正编号', 5.5, 'another unused summary', @created, @latest),
                        (-7, @blankTitle, 0.5, '', @created, @latest),
                        (9, '未评分', NULL, '', @created, @unratedLatest),
                        (-12, '平局负编号', 10, '', @created, @latest),
                        (73, '最早的评分', 5.5, '', @created, @earliest);
                    """;
                command.Parameters.AddWithValue("@blankTitle", " \t ");
                command.Parameters.AddWithValue("@created", "2026-01-01T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("@earliest", "2026-10-26T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("@older", "2026-10-27T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("@latest", "2026-10-28T00:00:00.0000000+00:00");
                command.Parameters.AddWithValue("@unratedLatest", "2026-10-29T00:00:00.0000000+00:00");
                await command.ExecuteNonQueryAsync();
            }
            var archive = new ArchiveService(database.Factory);
            var expected = (await archive.GetArchiveListAsync())
                .Where(item => item.Archive.PersonalRating is not null)
                .Select(item => (item.Archive.AnimeId, item.Archive.TitleSnapshot,
                    PersonalRating: item.Archive.PersonalRating!.Value))
                .ToArray();

            var actual = await archive.GetRatedArchiveSeedsAsync().WaitAsync(Budget);

            Assert.Equal(expected, actual);
            Assert.Equal(5, actual.Count);
            Assert.DoesNotContain(actual, item => item.AnimeId == 9);
            Assert.Equal(0.5, Assert.Single(actual, item => item.AnimeId == -7).PersonalRating);
            Assert.Equal(" \t ", Assert.Single(actual, item => item.AnimeId == -7).TitleSnapshot);
            Assert.Equal(string.Empty, Assert.Single(actual, item => item.AnimeId == 41).TitleSnapshot);
            Assert.Equal(5.5, Assert.Single(actual, item => item.AnimeId == 18).PersonalRating);
            Assert.Equal(10, Assert.Single(actual, item => item.AnimeId == -12).PersonalRating);
            Assert.Equal(new[] { -12, -7, 18 }, actual.Take(3).Select(item => item.AnimeId).Order());
            Assert.Equal(new[] { 41, 73 }, actual.Skip(3).Select(item => item.AnimeId));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreCancelledReadDoesNotCreateADatabase()
    {
        var database = new PersonalDatabaseFixture();
        try
        {
            var archive = new ArchiveService(database.Factory);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.False(File.Exists(database.Factory.DatabasePath));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive
                .GetRatedArchiveSeedsAsync(cancellation.Token).WaitAsync(Budget));

            Assert.False(File.Exists(database.Factory.DatabasePath));
        }
        finally
        {
            await database.DisposeAsync();
        }
    }
}
