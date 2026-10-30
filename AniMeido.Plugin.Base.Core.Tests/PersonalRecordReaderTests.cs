using AniMeido.Plugin.Base.Services;

namespace AniMeido.Tests;

public sealed class PersonalRecordReaderTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory DbFactory => _database.Factory;

    public Task InitializeAsync() => _database.InitializeAsync();

    public Task DisposeAsync() => _database.DisposeAsync();

    [Theory]
    [InlineData(null, new[] { -7, 1, 10 })]
    [InlineData(1, new[] { 1 })]
    [InlineData(-7, new[] { -7 })]
    [InlineData(404, new int[0])]
    [InlineData(0, new int[0])]
    public async Task IdentityQueryFiltersBeforeReturningDistinctWorkIds(int? requestedId, int[] expected)
    {
        using var connection = await DbFactory.OpenAsync();
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            INSERT INTO tracking VALUES(1,1,'2026-10-26'),(10,1,'2026-10-26'),(0,1,'2026-10-26');
            INSERT INTO anime_archives(AnimeId,TitleSnapshot,SummaryNote,CreatedAt,UpdatedAt)
            VALUES(1,'first','keep this note','2026-10-26','2026-10-26'),
                (-7,'local','keep this note too','2026-10-26','2026-10-26');
            INSERT INTO archive_entries(EntryId,AnimeId,OccurredAt,Body,CreatedAt,UpdatedAt)
            VALUES('fixture',-7,'2026-10-26','private body','2026-10-26','2026-10-26');
            """;
        await seed.ExecuteNonQueryAsync();

        var ids = await PersonalRecordReader.ReadAnimeIdsAsync(connection, null, default, requestedId);

        Assert.Equal(expected, ids.Order());
    }
}
